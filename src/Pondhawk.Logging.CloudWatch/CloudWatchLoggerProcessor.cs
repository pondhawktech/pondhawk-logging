// Copyright (c) Pond Hawk Technologies Inc. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System.Text;
using System.Threading.Channels;
using Amazon.CloudWatchLogs;
using Amazon.CloudWatchLogs.Model;
using Amazon.Runtime;
using CommunityToolkit.Diagnostics;
using Microsoft.Extensions.Logging;
using ZLogger;

namespace Pondhawk.Logging.CloudWatch;

/// <summary>
/// A ZLogger <see cref="IAsyncLogProcessor"/> that sends each event to CloudWatch Logs as one JSON object,
/// in the stream <c>&lt;instance-id&gt;/&lt;service&gt;</c> of a log group.
/// </summary>
/// <remarks>
/// <para>
/// It never throws into the caller and never holds the process up. Constructing it touches no network: the
/// AWS client is built, the stream name resolved and the stream created on the first batch, on the flush
/// thread — so a slow or unreachable CloudWatch cannot delay a host's startup, and a client that cannot be
/// built (no region configured, for one) turns CloudWatch off rather than stopping the service that asked
/// for it. The provider creates its own stream, and the group too if nobody has, giving a group it creates
/// <see cref="CloudWatchOptions.RetentionDays"/> of retention.
/// </para>
/// <para>
/// Anything that is a verdict on the configuration — no permission, no stream name — is noted once on
/// stdout and turns the provider off: for the process when it is about the process (no client, no instance
/// id), until the next <see cref="CloudWatchDestination.Rebind"/> when it is about the group. Any other
/// failure, to create the stream or to send a batch, pauses it for <see cref="PauseAfterFailure"/>, so an
/// outage costs the flush one bounded call a minute rather than one per batch. While paused, and while the
/// destination is unbound, the most recent Warning-and-above events are held
/// (<see cref="CloudWatchOptions.MaxHeldEvents"/>) and go out with the next batch that can be sent; lower
/// levels are discarded.
/// </para>
/// <para>
/// <see cref="Post"/> runs on the calling thread, so it is where the correlation id is read and the pooled
/// ZLogger entry copied out and returned. Formatting and sending happen on the flush thread.
/// </para>
/// </remarks>
public sealed class CloudWatchLoggerProcessor : IAsyncLogProcessor
{
    /// <summary>PutLogEvents: the most bytes in one request, counted as each message's UTF-8 bytes plus <see cref="PerEventOverhead"/>.</summary>
    public const int MaxBatchBytes = 1_048_576;

    /// <summary>PutLogEvents: the bytes each event counts for beyond its message.</summary>
    public const int PerEventOverhead = 26;

    /// <summary>PutLogEvents: the most events in one request.</summary>
    public const int MaxBatchEvents = 10_000;

    /// <summary>PutLogEvents: the events of one request may not span more than this.</summary>
    public static readonly TimeSpan MaxBatchSpan = TimeSpan.FromHours(24);

    /// <summary>How long CloudWatch is left alone after a call to it fails.</summary>
    public static readonly TimeSpan PauseAfterFailure = TimeSpan.FromMinutes(1);

    private readonly Func<IAmazonCloudWatchLogs> _clientFactory;
    private readonly CloudWatchDestination _destination;
    private readonly string _service;
    private readonly CloudWatchOptions _options;
    private readonly Func<string?> _instanceId;
    private readonly bool _ownsClient;
    private readonly Func<DateTime> _utcNow;
    private readonly Action<string> _note;

    private readonly Channel<CloudWatchEvent> _channel;
    private readonly TaskCompletionSource<bool> _flushCompleted = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _disposed;

    private enum State { Pending, Ready, Off }

    private enum Outcome { Done, GroupMissing, Final, Transient }

    // Touched by the flush thread only, apart from the volatile reads behind the diagnostic properties.
    private IAmazonCloudWatchLogs? _client;
    private volatile string? _stream;
    private volatile State _state = State.Pending;
    private volatile bool _offForProcess;
    private long _boundVersion;
    private long _pausedUntilTicks;

    private readonly object _heldLock = new();
    private readonly Queue<Outgoing> _held = new();

    /// <summary>Initializes a processor writing to <paramref name="destination"/>.</summary>
    /// <param name="clientFactory">
    /// Builds the CloudWatch Logs client, called once on the first batch. It may throw: that turns
    /// CloudWatch off rather than failing the caller.
    /// </param>
    /// <param name="destination">The log group to write to, re-read for every batch.</param>
    /// <param name="service">The service name — the second part of the default stream name.</param>
    /// <param name="options">The CloudWatch options.</param>
    /// <param name="instanceId">
    /// Reads the instance id — the first part of the default stream name — called once on the first batch.
    /// Not called when <see cref="CloudWatchOptions.StreamName"/> is set.
    /// </param>
    /// <param name="ownsClient">When <see langword="true"/>, the processor disposes the client it built on disposal.</param>
    public CloudWatchLoggerProcessor(
        Func<IAmazonCloudWatchLogs> clientFactory,
        CloudWatchDestination destination,
        string service,
        CloudWatchOptions options,
        Func<string?> instanceId,
        bool ownsClient = false)
        : this(clientFactory, destination, service, options, instanceId, ownsClient, utcNow: null, note: null)
    {
    }

    /// <summary>As the public constructor, with the clock and the stdout notes replaceable for tests.</summary>
    internal CloudWatchLoggerProcessor(
        Func<IAmazonCloudWatchLogs> clientFactory,
        CloudWatchDestination destination,
        string service,
        CloudWatchOptions options,
        Func<string?> instanceId,
        bool ownsClient,
        Func<DateTime>? utcNow,
        Action<string>? note)
    {
        Guard.IsNotNull(clientFactory);
        Guard.IsNotNull(destination);
        Guard.IsNotNull(service);
        Guard.IsNotNull(options);
        Guard.IsNotNull(instanceId);

        _clientFactory = clientFactory;
        _destination = destination;
        _service = service;
        _options = options;
        _instanceId = instanceId;
        _ownsClient = ownsClient;
        _utcNow = utcNow ?? (static () => DateTime.UtcNow);
        _note = note ?? WriteNote;
        _boundVersion = destination.Current.Version;

        _channel = Channel.CreateUnbounded<CloudWatchEvent>(new UnboundedChannelOptions
        {
            SingleWriter = false,
            SingleReader = true,
        });

        _ = Task.Run(FlushLoopAsync);
    }

    /// <summary>Gets the stream being written to, or an empty string before the first batch resolves it.</summary>
    public string LogStream => _stream ?? string.Empty;

    /// <summary>Gets whether the stream exists and batches are being sent.</summary>
    public bool IsReady => !_offForProcess && _state == State.Ready;

    /// <summary>
    /// Gets whether CloudWatch has been turned off by a verdict on the configuration: for the process, or
    /// for the current log group until a rebind names another.
    /// </summary>
    public bool IsOff => _offForProcess || _state == State.Off;

    /// <summary>Gets whether a failure has CloudWatch left alone for now.</summary>
    public bool IsPaused => _utcNow().Ticks < Interlocked.Read(ref _pausedUntilTicks);

    /// <summary>Gets the number of Warning-and-above events held for the next batch that can be sent.</summary>
    public int HeldEventCount
    {
        get
        {
            lock (_heldLock)
                return _held.Count;
        }
    }

    /// <summary>
    /// Copies a ZLogger entry out on the calling thread (capturing correlation) and queues it for batched
    /// delivery, then returns the pooled entry.
    /// </summary>
    /// <param name="log">The ZLogger entry.</param>
    public void Post(IZLoggerEntry log)
    {
        try
        {
            if (Volatile.Read(ref _disposed) != 0)
                return;

            _channel.Writer.TryWrite(Capture(log));
        }
        catch
        {
            // A single malformed entry must never crash the caller's logging path.
        }
        finally
        {
            log.Return();
        }
    }

    private static CloudWatchEvent Capture(IZLoggerEntry entry)
    {
        var info = entry.LogInfo;

        var logEvent = new CloudWatchEvent
        {
            Level = info.LogLevel,
            Category = info.Category.Name,
            Title = entry.ToString(),
            CorrelationId = CorrelationManager.Current,
            Occurred = info.Timestamp.Utc.UtcDateTime,
            Exception = info.Exception,
        };

        for (var i = 0; i < entry.ParameterCount; i++)
        {
            var key = entry.GetParameterKeyAsString(i);
            var value = entry.GetParameterValue(i);

            switch (key)
            {
                case LogPropertyNames.Nesting when value is int nesting:
                    logEvent.Nesting = nesting;
                    break;
                case LogPropertyNames.PayloadType when value is int payloadType:
                    logEvent.PayloadType = (PayloadType)payloadType;
                    break;
                case LogPropertyNames.PayloadContent:
                    logEvent.Payload = value as string ?? value?.ToString();
                    break;
                case LogPropertyNames.ErrorContext:
                    logEvent.ErrorContext = value as string ?? value?.ToString();
                    break;
            }
        }

        return logEvent;
    }

    private async Task FlushLoopAsync()
    {
        var batch = new List<CloudWatchEvent>(_options.BatchSize);
        var reader = _channel.Reader;

        try
        {
            while (true)
            {
                batch.Clear();

                if (!await reader.WaitToReadAsync().ConfigureAwait(false))
                    break;

                using var timeoutCts = new CancellationTokenSource(_options.FlushInterval);

                try
                {
                    while (batch.Count < _options.BatchSize)
                    {
                        if (reader.TryRead(out var logEvent))
                        {
                            batch.Add(logEvent);
                        }
                        else if (!await reader.WaitToReadAsync(timeoutCts.Token).ConfigureAwait(false))
                        {
                            break;
                        }
                    }
                }
                catch (OperationCanceledException)
                {
                    // Timeout expired, flush what we have.
                }

                if (batch.Count > 0)
                {
                    try
                    {
                        await SendAsync(batch).ConfigureAwait(false);
                    }
                    catch
                    {
                        // Backstop: the drain loop must outlive any single batch failure.
                    }
                }
            }
        }
        finally
        {
            _flushCompleted.TrySetResult(true);
        }
    }

    private async Task SendAsync(List<CloudWatchEvent> batch)
    {
        if (_offForProcess)
            return;

        // One snapshot for the whole send, so the group the stream was made in and the group the batch is
        // put to cannot disagree even if a rebind lands mid-flight.
        var binding = _destination.Current;

        if (binding.IsUnbound)
        {
            // No group named yet. This is a configured state, not a failure: nothing is called and nothing
            // is noted, and the events describing how the process came up survive to the rebind.
            Hold(batch.Select(ToOutgoing));
            return;
        }

        if (binding.Version != _boundVersion)
        {
            // A different group: its stream is still to be made, and a denial or a pause earned against
            // the old group says nothing about this one.
            _boundVersion = binding.Version;
            _state = State.Pending;
            Interlocked.Exchange(ref _pausedUntilTicks, 0);
        }

        if (_state == State.Off)
            return;

        if (IsPaused)
        {
            Hold(batch.Select(ToOutgoing));
            return;
        }

        if (_state == State.Pending)
            await ConnectAsync(binding.LogGroup).ConfigureAwait(false);

        if (IsOff)
            return;

        var outgoing = batch.Select(ToOutgoing);

        if (_state != State.Ready)
        {
            Hold(outgoing);
            return;
        }

        // Held events go out with the first batch that can, in time order with it.
        var events = TakeHeld().Concat(outgoing).OrderBy(e => e.Input.Timestamp).ToList();
        await PutAsync(binding.LogGroup, Chunk(events).ToList()).ConfigureAwait(false);
    }

    private async Task PutAsync(string logGroup, List<List<Outgoing>> chunks)
    {
        for (var i = 0; i < chunks.Count; i++)
        {
            try
            {
                await _client!.PutLogEventsAsync(new PutLogEventsRequest
                {
                    LogGroupName = logGroup,
                    LogStreamName = _stream,
                    LogEvents = chunks[i].ConvertAll(e => e.Input),
                }).ConfigureAwait(false);
            }
            catch (Exception cause)
            {
                // The group or stream was deleted under us: make them again once the pause is over.
                if (cause is ResourceNotFoundException)
                    _state = State.Pending;

                // The rest of this batch would most likely fail the same way.
                Pause(logGroup, $"could not send a batch ({cause.Message})");
                Hold(chunks.Skip(i).SelectMany(c => c));
                return;
            }
        }
    }

    /// <summary>
    /// Builds the client and resolves the stream name, once per process, then creates the stream — and the
    /// group first if it is missing. The answer is one of: ready, off (a verdict on the configuration), or
    /// paused and tried again after the pause (anything else).
    /// </summary>
    private async Task ConnectAsync(string logGroup)
    {
        if (_client is null)
        {
            try
            {
                _client = _clientFactory();
            }
            catch (Exception cause)
            {
                _offForProcess = true;
                _note($"CloudWatch logging ({logGroup}): could not build a CloudWatch Logs client ({cause.Message}). CloudWatch logging is off until restart");
                return;
            }
        }

        if (_stream is null)
        {
            var stream = ResolveStream(logGroup);
            if (stream is null)
            {
                _offForProcess = true;
                return;
            }

            _stream = stream;
        }

        var outcome = await CreateStreamAsync(logGroup).ConfigureAwait(false);
        if (outcome == Outcome.GroupMissing)
        {
            outcome = await CreateGroupAsync(logGroup).ConfigureAwait(false);
            if (outcome == Outcome.Done)
                outcome = await CreateStreamAsync(logGroup).ConfigureAwait(false);
        }

        _state = outcome switch
        {
            Outcome.Done => State.Ready,
            Outcome.Transient => State.Pending,
            _ => State.Off,
        };
    }

    private string? ResolveStream(string logGroup)
    {
        if (!string.IsNullOrWhiteSpace(_options.StreamName))
            return _options.StreamName;

        string? instanceId;
        try
        {
            instanceId = _instanceId();
        }
        catch (Exception cause)
        {
            instanceId = null;
            _note($"CloudWatch logging ({logGroup}): could not read the instance id ({cause.Message})");
        }

        if (string.IsNullOrWhiteSpace(instanceId))
        {
            _note($"CloudWatch logging ({logGroup}): no instance id -- not on EC2? Set CloudWatchOptions.StreamName to log from here. CloudWatch logging is off until restart");
            return null;
        }

        return $"{instanceId}/{_service}";
    }

    private async Task<Outcome> CreateStreamAsync(string logGroup)
    {
        try
        {
            await _client!.CreateLogStreamAsync(new CreateLogStreamRequest { LogGroupName = logGroup, LogStreamName = _stream }).ConfigureAwait(false);
            return Outcome.Done;
        }
        catch (ResourceAlreadyExistsException)
        {
            // The service restarted; its stream is still there.
            return Outcome.Done;
        }
        catch (ResourceNotFoundException)
        {
            return Outcome.GroupMissing;
        }
        catch (Exception cause) when (IsVerdict(cause))
        {
            _note($"CloudWatch logging ({logGroup}): cannot create stream {_stream} ({cause.Message}). CloudWatch logging is off for this group");
            return Outcome.Final;
        }
        catch (Exception cause)
        {
            // Not a verdict on the configuration -- a timeout, a throttle, an outage.
            Pause(logGroup, $"could not create stream {_stream} ({cause.Message})");
            return Outcome.Transient;
        }
    }

    /// <summary>
    /// Creates the group, and gives it its retention — only if this host is the one that created it, so an
    /// operator's own retention on an existing group is never touched.
    /// </summary>
    private async Task<Outcome> CreateGroupAsync(string logGroup)
    {
        try
        {
            await _client!.CreateLogGroupAsync(new CreateLogGroupRequest { LogGroupName = logGroup }).ConfigureAwait(false);
        }
        catch (ResourceAlreadyExistsException)
        {
            // Another host created it first. Its retention is that host's business.
            return Outcome.Done;
        }
        catch (Exception cause) when (IsVerdict(cause))
        {
            _note($"CloudWatch logging ({logGroup}): the log group does not exist and this host may not create it ({cause.Message}). Create it or grant logs:CreateLogGroup. CloudWatch logging is off for this group");
            return Outcome.Final;
        }
        catch (Exception cause)
        {
            Pause(logGroup, $"could not create the log group ({cause.Message})");
            return Outcome.Transient;
        }

        try
        {
            await _client!.PutRetentionPolicyAsync(new PutRetentionPolicyRequest { LogGroupName = logGroup, RetentionInDays = _options.RetentionDays }).ConfigureAwait(false);
        }
        catch (Exception cause)
        {
            // The group exists and can be written to; it will simply keep events until someone sets its
            // retention.
            _note($"CloudWatch logging ({logGroup}): created the log group but could not set its retention to {_options.RetentionDays} days ({cause.Message}). Set it by hand");
        }

        return Outcome.Done;
    }

    // Denied, or told the request itself is wrong: asking again will get the same answer.
    private static bool IsVerdict(Exception cause) =>
        cause is AccessDeniedException or InvalidParameterException
        || cause is AmazonServiceException { ErrorCode: "AccessDeniedException" or "UnrecognizedClientException" };

    private static Outgoing ToOutgoing(CloudWatchEvent logEvent) =>
        new(
            new InputLogEvent
            {
                Timestamp = DateTime.SpecifyKind(logEvent.Occurred, DateTimeKind.Utc),
                Message = CloudWatchEventFormatter.Format(logEvent),
            },
            logEvent.Level >= LogLevel.Warning);

    /// <summary>Keeps the Warning-and-above events of <paramref name="events"/>, the most recent under the cap.</summary>
    private void Hold(IEnumerable<Outgoing> events)
    {
        lock (_heldLock)
        {
            foreach (var e in events.Where(e => e.Hold))
            {
                _held.Enqueue(e);
                while (_held.Count > _options.MaxHeldEvents)
                    _held.Dequeue();
            }
        }
    }

    private List<Outgoing> TakeHeld()
    {
        lock (_heldLock)
        {
            var held = _held.ToList();
            _held.Clear();
            return held;
        }
    }

    /// <summary>Splits time-ordered events into requests within PutLogEvents' limits.</summary>
    internal static IEnumerable<List<Outgoing>> Chunk(IReadOnlyList<Outgoing> events)
    {
        var chunk = new List<Outgoing>();
        var bytes = 0;

        foreach (var e in events)
        {
            var size = Encoding.UTF8.GetByteCount(e.Input.Message) + PerEventOverhead;
            if (chunk.Count > 0
                && (bytes + size > MaxBatchBytes
                    || chunk.Count >= MaxBatchEvents
                    || e.Input.Timestamp - chunk[0].Input.Timestamp >= MaxBatchSpan))
            {
                yield return chunk;
                chunk = [];
                bytes = 0;
            }

            chunk.Add(e);
            bytes += size;
        }

        if (chunk.Count > 0)
            yield return chunk;
    }

    private void Pause(string logGroup, string message)
    {
        Interlocked.Exchange(ref _pausedUntilTicks, (_utcNow() + PauseAfterFailure).Ticks);
        _note($"CloudWatch logging ({logGroup}): {message}. Not sending to CloudWatch for {PauseAfterFailure.TotalMinutes:0} minute(s); warnings and errors are held, the most recent {_options.MaxHeldEvents}");
    }

    // Straight to stdout: logging through the factory here would feed the failure back into the provider
    // that is failing. At warning priority under journald, which systemd announces with JOURNAL_STREAM.
    private static void WriteNote(string message)
    {
        var prefix = string.IsNullOrEmpty(Environment.GetEnvironmentVariable("JOURNAL_STREAM")) ? string.Empty : "<4>";
        System.Console.WriteLine(prefix + message);
    }

    /// <summary>Completes the channel, waits for pending batches to be sent, and disposes the client if owned.</summary>
    /// <returns>A <see cref="ValueTask"/> representing the asynchronous dispose operation.</returns>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        _channel.Writer.Complete();

        try
        {
            await _flushCompleted.Task.WaitAsync(TimeSpan.FromSeconds(10)).ConfigureAwait(false);
        }
        catch
        {
            // Ignore exceptions during disposal (including timeout).
        }

        if (_ownsClient)
            _client?.Dispose();
    }

    /// <summary>One formatted event, and whether it is worth holding when it cannot be sent.</summary>
    internal readonly record struct Outgoing(InputLogEvent Input, bool Hold);
}
