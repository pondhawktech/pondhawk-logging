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
/// id), until the next <see cref="CloudWatchDestination.Rebind"/> when it is about the group. A request
/// CloudWatch refuses as invalid is dropped, never sent again: one bad batch must not hold up the rest. Any
/// other failure, to create the stream or to send a batch, pauses the provider for
/// <see cref="PauseAfterFailure"/>, so an outage costs the flush one bounded call a minute rather than one
/// per batch. While paused, and while the destination is unbound, the most recent Warning-and-above events
/// are held (<see cref="CloudWatchOptions.MaxHeldEvents"/>); lower levels are discarded. Held events go out
/// as soon as they can — with the next batch, or on their own once the pause is over — and get one last
/// attempt when the provider is disposed.
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

    /// <summary>
    /// How many times the instance id is asked for, a pause apart, before its absence is taken as a verdict:
    /// instance metadata can be briefly unreachable while a host is still coming up.
    /// </summary>
    public const int MaxInstanceIdAttempts = 3;

    /// <summary>How often held events are looked at again when no new event arrives to carry them out.</summary>
    internal static readonly TimeSpan DefaultHeldRetryInterval = TimeSpan.FromSeconds(5);

    private static readonly TimeSpan DefaultFlushInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan MaxFlushInterval = TimeSpan.FromHours(1);

    private readonly Func<IAmazonCloudWatchLogs> _clientFactory;
    private readonly CloudWatchDestination _destination;
    private readonly string _service;
    private readonly Func<string?> _instanceId;
    private readonly bool _ownsClient;
    private readonly Func<DateTime> _utcNow;
    private readonly Action<string> _note;

    // The options, read once and made safe: a value that makes no sense must not be able to stop the flush
    // loop, which nothing would notice.
    private readonly string? _streamName;
    private readonly int _retentionDays;
    private readonly int _batchSize;
    private readonly TimeSpan _flushInterval;
    private readonly int _maxHeld;
    private readonly TimeSpan _heldRetryInterval;

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
    private int _instanceIdAttempts;
    private long _quietUntilTicks;

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
        : this(clientFactory, destination, service, options, instanceId, ownsClient, utcNow: null, note: null, heldRetryInterval: null)
    {
    }

    /// <summary>As the public constructor, with the clock, the stdout notes and the held-event retry replaceable for tests.</summary>
    internal CloudWatchLoggerProcessor(
        Func<IAmazonCloudWatchLogs> clientFactory,
        CloudWatchDestination destination,
        string service,
        CloudWatchOptions options,
        Func<string?> instanceId,
        bool ownsClient,
        Func<DateTime>? utcNow,
        Action<string>? note,
        TimeSpan? heldRetryInterval)
    {
        Guard.IsNotNull(clientFactory);
        Guard.IsNotNull(destination);
        Guard.IsNotNull(service);
        Guard.IsNotNull(options);
        Guard.IsNotNull(instanceId);

        _clientFactory = clientFactory;
        _destination = destination;
        _service = service;
        _instanceId = instanceId;
        _ownsClient = ownsClient;
        _utcNow = utcNow ?? (static () => DateTime.UtcNow);
        _note = note ?? WriteNote;
        _boundVersion = destination.Current.Version;

        _streamName = string.IsNullOrWhiteSpace(options.StreamName) ? null : options.StreamName;
        _retentionDays = options.RetentionDays;
        _batchSize = Math.Clamp(options.BatchSize, 1, MaxBatchEvents);
        _flushInterval = options.FlushInterval < TimeSpan.Zero
            ? DefaultFlushInterval
            : options.FlushInterval > MaxFlushInterval ? MaxFlushInterval : options.FlushInterval;
        _maxHeld = Math.Max(0, options.MaxHeldEvents);
        _heldRetryInterval = heldRetryInterval is { } retry && retry > TimeSpan.Zero ? retry : DefaultHeldRetryInterval;

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

            // The filter AddCloudWatch registers keeps Debug and Trace away, but a more specific rule in the
            // host's configuration would outrank it. This is what makes "never" true.
            if (log.LogInfo.LogLevel < CloudWatchLoggingBuilderExtensions.MinimumLevel)
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
            Subject = CorrelationManager.Subject,
            Tenant = CorrelationManager.Tenant,
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
        var batch = new List<CloudWatchEvent>(Math.Min(_batchSize, 1_024));
        var reader = _channel.Reader;

        try
        {
            while (true)
            {
                try
                {
                    batch.Clear();

                    if (!await WaitForWorkAsync(reader).ConfigureAwait(false))
                        break;

                    using var timeoutCts = new CancellationTokenSource(_flushInterval);

                    try
                    {
                        while (batch.Count < _batchSize)
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

                    await SendAsync(batch, final: false).ConfigureAwait(false);
                }
                catch
                {
                    // Backstop: the drain loop must outlive any single batch failure.
                }
            }

            try
            {
                // Shutting down: one last attempt for what is still held, pause or no pause.
                batch.Clear();
                await SendAsync(batch, final: true).ConfigureAwait(false);
            }
            catch
            {
                // Nothing more can be done for it.
            }
        }
        finally
        {
            _flushCompleted.TrySetResult(true);
        }
    }

    /// <summary>
    /// Waits for an event to arrive. With events held it waits only <see cref="_heldRetryInterval"/>, so
    /// they go out once they can even if nothing else is ever logged. False when the channel is finished.
    /// </summary>
    private async Task<bool> WaitForWorkAsync(ChannelReader<CloudWatchEvent> reader)
    {
        if (HeldEventCount == 0)
            return await reader.WaitToReadAsync().ConfigureAwait(false);

        using var retryCts = new CancellationTokenSource(_heldRetryInterval);

        try
        {
            return await reader.WaitToReadAsync(retryCts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return true;
        }
    }

    private async Task SendAsync(List<CloudWatchEvent> batch, bool final)
    {
        if (_offForProcess || (batch.Count == 0 && HeldEventCount == 0))
            return;

        // One snapshot for the whole send, so the group the stream was made in and the group the batch is
        // put to cannot disagree even if a rebind lands mid-flight.
        var binding = _destination.Current;

        if (binding.IsUnbound)
        {
            // No group named yet. This is a configured state, not a failure: nothing is called and nothing
            // is noted, and the events describing how the process came up survive to the rebind.
            Hold(batch);
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

        if (IsPaused && !final)
        {
            Hold(batch);
            return;
        }

        if (_state == State.Pending)
            await ConnectAsync(binding.LogGroup).ConfigureAwait(false);

        if (IsOff)
            return;

        if (_state != State.Ready)
        {
            Hold(batch);
            return;
        }

        // Formatted before the held events are taken, so nothing that goes wrong here can cost them. Held
        // events go out with the first batch that can, in time order with it.
        var outgoing = batch.ConvertAll(ToOutgoing);
        var events = TakeHeld().Concat(outgoing).OrderBy(e => e.Input.Timestamp).ToList();

        if (events.Count > 0)
            await PutAsync(binding.LogGroup, Chunk(events).ToList()).ConfigureAwait(false);
    }

    private async Task PutAsync(string logGroup, List<List<Outgoing>> chunks)
    {
        for (var i = 0; i < chunks.Count; i++)
        {
            try
            {
                var response = await _client!.PutLogEventsAsync(new PutLogEventsRequest
                {
                    LogGroupName = logGroup,
                    LogStreamName = _stream,
                    LogEvents = chunks[i].ConvertAll(e => e.Input),
                }).ConfigureAwait(false);

                if (response?.RejectedLogEventsInfo is { } rejected)
                    NoteRejected(logGroup, rejected);
            }
            catch (InvalidParameterException cause)
            {
                // A verdict on this request, not on CloudWatch: sending it again would get the same answer,
                // and holding it would put it at the head of every batch from here on. It is dropped, and
                // the rest goes on.
                NoteQuietly(logGroup, $"CloudWatch refused a batch of {chunks[i].Count} event(s) as invalid and it was dropped ({cause.Message})");
            }
            catch (Exception cause) when (IsDenied(cause))
            {
                _state = State.Off;
                Note(logGroup, $"not allowed to put events to stream {_stream} ({cause.Message}). Grant logs:PutLogEvents. CloudWatch logging is off for this group");
                return;
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
                Note(logGroup, $"could not build a CloudWatch Logs client ({cause.Message}). CloudWatch logging is off until restart");
                return;
            }
        }

        if (_stream is null)
        {
            var stream = ResolveStream(logGroup);
            if (stream is null)
                return;

            _stream = stream;
        }

        var outcome = await CreateStreamAsync(logGroup).ConfigureAwait(false);
        if (outcome == Outcome.GroupMissing)
        {
            outcome = await CreateGroupAsync(logGroup).ConfigureAwait(false);
            if (outcome == Outcome.Done)
                outcome = await CreateStreamAsync(logGroup).ConfigureAwait(false);

            if (outcome == Outcome.GroupMissing)
            {
                // Created, or found to exist, and still not there for the stream: not yet visible, or
                // deleted in between. Either way not a verdict.
                Pause(logGroup, "the log group was created but is not there yet");
                outcome = Outcome.Transient;
            }
        }

        _state = outcome switch
        {
            Outcome.Done => State.Ready,
            Outcome.Transient => State.Pending,
            _ => State.Off,
        };
    }

    /// <summary>
    /// The stream name: the one the host supplied, or <c>&lt;instance-id&gt;/&lt;service&gt;</c>. Null when
    /// there is no instance id — tried again after a pause, <see cref="MaxInstanceIdAttempts"/> times in
    /// all, and only then taken as a verdict that turns CloudWatch off for the process.
    /// </summary>
    private string? ResolveStream(string logGroup)
    {
        if (_streamName is not null)
            return _streamName;

        string? instanceId;
        var why = "no instance id -- not on EC2?";
        try
        {
            instanceId = _instanceId();
        }
        catch (Exception cause)
        {
            instanceId = null;
            why = $"could not read the instance id ({cause.Message})";
        }

        if (!string.IsNullOrWhiteSpace(instanceId))
            return $"{instanceId}/{_service}";

        if (++_instanceIdAttempts < MaxInstanceIdAttempts)
        {
            // Instance metadata can be out of reach for a moment while a host comes up. Quietly: it is not
            // yet known to be a problem.
            Interlocked.Exchange(ref _pausedUntilTicks, (_utcNow() + PauseAfterFailure).Ticks);
            return null;
        }

        _offForProcess = true;
        Note(logGroup, $"{why} Asked {MaxInstanceIdAttempts} times. Set CloudWatchOptions.StreamName to log from here. CloudWatch logging is off until restart");
        return null;
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
        catch (Exception cause) when (IsDenied(cause) || cause is InvalidParameterException)
        {
            Note(logGroup, $"cannot create stream {_stream} ({cause.Message}). CloudWatch logging is off for this group");
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
        catch (Exception cause) when (IsDenied(cause) || cause is InvalidParameterException)
        {
            Note(logGroup, $"the log group does not exist and this host may not create it ({cause.Message}). Create it or grant logs:CreateLogGroup. CloudWatch logging is off for this group");
            return Outcome.Final;
        }
        catch (Exception cause)
        {
            Pause(logGroup, $"could not create the log group ({cause.Message})");
            return Outcome.Transient;
        }

        try
        {
            await _client!.PutRetentionPolicyAsync(new PutRetentionPolicyRequest { LogGroupName = logGroup, RetentionInDays = _retentionDays }).ConfigureAwait(false);
        }
        catch (Exception cause)
        {
            // The group exists and can be written to; it will simply keep events until someone sets its
            // retention.
            Note(logGroup, $"created the log group but could not set its retention to {_retentionDays} days ({cause.Message}). Set it by hand");
        }

        return Outcome.Done;
    }

    private static bool IsDenied(Exception cause) =>
        cause is AccessDeniedException
        || cause is AmazonServiceException { ErrorCode: "AccessDeniedException" or "UnrecognizedClientException" };

    private static Outgoing ToOutgoing(CloudWatchEvent logEvent) =>
        new(
            new InputLogEvent
            {
                Timestamp = DateTime.SpecifyKind(logEvent.Occurred, DateTimeKind.Utc),
                Message = CloudWatchEventFormatter.Format(logEvent),
            },
            logEvent.Level >= LogLevel.Warning);

    /// <summary>
    /// Keeps the Warning-and-above events of <paramref name="batch"/>. Only those are formatted: during an
    /// outage everything else is discarded, and is not worth the work first.
    /// </summary>
    private void Hold(List<CloudWatchEvent> batch)
    {
        if (_maxHeld == 0)
            return;

        foreach (var logEvent in batch)
        {
            if (logEvent.Level >= LogLevel.Warning)
                Hold(ToOutgoing(logEvent));
        }
    }

    /// <summary>Keeps the Warning-and-above events of a send that failed.</summary>
    private void Hold(IEnumerable<Outgoing> events)
    {
        foreach (var e in events)
        {
            if (e.Hold)
                Hold(e);
        }
    }

    // The most recent under the cap.
    private void Hold(Outgoing e)
    {
        if (_maxHeld == 0)
            return;

        lock (_heldLock)
        {
            _held.Enqueue(e);
            while (_held.Count > _maxHeld)
                _held.Dequeue();
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
        Note(logGroup, $"{message}. Not sending to CloudWatch for {PauseAfterFailure.TotalMinutes:0} minute(s); warnings and errors are held, the most recent {_maxHeld}");
    }

    private void NoteRejected(string logGroup, RejectedLogEventsInfo rejected)
    {
        var why = rejected.TooNewLogEventStartIndex is not null
            ? "some as too far in the future"
            : "some as too old or past the group's retention";

        NoteQuietly(logGroup, $"CloudWatch accepted a batch but rejected {why}. Check this host's clock");
    }

    private void Note(string logGroup, string message)
    {
        // One line, whatever a message from AWS contains: under journald a second line would carry its own
        // priority.
        var line = $"CloudWatch logging ({logGroup}): {message}";
        _note(line.Replace('\r', ' ').Replace('\n', ' '));
    }

    /// <summary>As <see cref="Note"/>, at most once a pause: for what could otherwise be said per batch.</summary>
    private void NoteQuietly(string logGroup, string message)
    {
        var now = _utcNow().Ticks;
        if (now < Interlocked.Read(ref _quietUntilTicks))
            return;

        Interlocked.Exchange(ref _quietUntilTicks, now + PauseAfterFailure.Ticks);
        Note(logGroup, message);
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
