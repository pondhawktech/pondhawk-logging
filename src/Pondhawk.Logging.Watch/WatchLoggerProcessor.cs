// Copyright (c) Pond Hawk Technologies Inc. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Globalization;
using System.Net.Http.Headers;
using System.Threading.Channels;
using CommunityToolkit.Diagnostics;
using Microsoft.Extensions.Logging;
using ZLogger;

namespace Pondhawk.Logging.Watch;

/// <summary>
/// A ZLogger <see cref="IAsyncLogProcessor"/> that delivers log entries to the Watch Server with
/// Channel-based batching, HTTP posting, and a circuit breaker.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="Post"/> runs on the <em>calling</em> thread, so it is where the correlation id is read from
/// <see cref="Activity.Current"/> (which is gone by the time the background flush loop runs) and where the
/// ZLogger entry — a pooled object — is converted to a <see cref="LogEvent"/> and returned. The converted
/// events are queued to an unbounded channel; a background task drains it by batch size or flush interval
/// and posts the batches.
/// </para>
/// <para>
/// Level filtering is <em>not</em> done here: the switch decision is a Microsoft.Extensions.Logging filter
/// that gates <c>IsEnabled</c> at the call site (so a switch-dropped category is never formatted). The
/// switch is consulted here only for the per-event color and tag.
/// </para>
/// </remarks>
public sealed class WatchLoggerProcessor : IAsyncLogProcessor
{
    private readonly HttpClient _client;
    private readonly SwitchSource _switchSource;
    private readonly bool _ownsDependencies;
    private readonly string _domain;
    private readonly int _batchSize;
    private readonly TimeSpan _flushInterval;

    private readonly Channel<LogEvent> _channel;
    private readonly Task _flushTask;
    private readonly TaskCompletionSource<bool> _flushCompleted = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private int _disposed;

    // Circuit breaker state
    private int _consecutiveFailures;
    private DateTime _circuitOpenUntil = DateTime.MinValue;
    private readonly object _circuitLock = new();

    // Critical event buffer
    private readonly ConcurrentQueue<LogEvent> _criticalBuffer = new();
    private long _droppedEventCount;

    /// <summary>Gets or sets the failure threshold before the circuit opens.</summary>
    public int FailureThreshold { get; set; } = 3;

    /// <summary>Gets or sets the base delay before retrying after the circuit opens.</summary>
    public TimeSpan BaseRetryDelay { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>Gets or sets the maximum retry delay.</summary>
    public TimeSpan MaxRetryDelay { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>Gets or sets the maximum number of critical events to buffer during an outage.</summary>
    public int MaxCriticalBufferSize { get; set; } = 1000;

    /// <summary>Gets the current circuit state.</summary>
    public bool IsCircuitOpen
    {
        get
        {
            lock (_circuitLock)
            {
                return _circuitOpenUntil > DateTime.UtcNow;
            }
        }
    }

    /// <summary>Gets the number of events currently in the critical buffer.</summary>
    public int CriticalBufferCount => _criticalBuffer.Count;

    /// <summary>Gets the total number of events dropped due to buffer overflow.</summary>
    public long DroppedEventCount => Interlocked.Read(ref _droppedEventCount);

    /// <summary>Initializes a new instance of the <see cref="WatchLoggerProcessor"/> class.</summary>
    /// <param name="client">The <see cref="HttpClient"/> used to post event batches to the Watch Server.</param>
    /// <param name="switchSource">The switch source consulted for per-event color and tag.</param>
    /// <param name="domain">The domain name included in each batch.</param>
    /// <param name="batchSize">Maximum events per batch before flushing.</param>
    /// <param name="flushInterval">Maximum time before flushing a partial batch. Defaults to 100ms.</param>
    /// <param name="ownsDependencies">
    /// When <see langword="true"/>, the processor disposes <paramref name="switchSource"/> and
    /// <paramref name="client"/> on disposal; otherwise it only stops the switch source.
    /// </param>
    public WatchLoggerProcessor(
        HttpClient client,
        SwitchSource switchSource,
        string domain,
        int batchSize = 100,
        TimeSpan? flushInterval = null,
        bool ownsDependencies = false)
    {
        Guard.IsNotNull(client);
        Guard.IsNotNull(switchSource);
        Guard.IsNotNull(domain);

        _client = client;
        _switchSource = switchSource;
        _ownsDependencies = ownsDependencies;
        _domain = domain;
        _batchSize = batchSize;
        _flushInterval = flushInterval ?? TimeSpan.FromMilliseconds(100);

        _channel = Channel.CreateUnbounded<LogEvent>(new UnboundedChannelOptions
        {
            SingleWriter = false,
            SingleReader = true,
        });

        _flushTask = Task.Run(FlushLoopAsync);
    }

    /// <summary>
    /// Converts a ZLogger entry to a <see cref="LogEvent"/> on the calling thread (capturing correlation)
    /// and queues it for batched delivery, then returns the pooled entry.
    /// </summary>
    /// <param name="log">The ZLogger entry.</param>
    public void Post(IZLoggerEntry log)
    {
        try
        {
            if (Volatile.Read(ref _disposed) != 0)
                return;

            var correlationId = GetCorrelationId();
            var logEvent = ConvertEntry(log, correlationId);
            _channel.Writer.TryWrite(logEvent);
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

    private LogEvent ConvertEntry(IZLoggerEntry entry, string correlationId)
    {
        var info = entry.LogInfo;
        var category = string.IsNullOrEmpty(info.Category.Name) ? "ZLogger" : info.Category.Name;
        var sw = _switchSource.Lookup(category);

        var logEvent = new LogEvent
        {
            Category = category,
            Level = (int)info.LogLevel,
            Color = sw.Color.ToArgb(),
            Tag = sw.Tag,
            Title = entry.ToString(),
            CorrelationId = correlationId,
            Occurred = info.Timestamp.Utc.UtcDateTime,
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
                    logEvent.Type = payloadType;
                    break;
                case LogPropertyNames.PayloadContent:
                    logEvent.Payload = value as string ?? value?.ToString();
                    break;
            }
        }

        // If no explicit payload was attached but the event carries an exception, transmit its full detail.
        if (logEvent.Payload is null && info.Exception is not null)
        {
            var exception = info.Exception;
            logEvent.ErrorType = exception.GetType().FullName ?? exception.GetType().Name;
            logEvent.Type = (int)PayloadType.Text;
            logEvent.Payload = exception.ToString();
        }

        return logEvent;
    }

    private static string GetCorrelationId()
    {
        var activity = Activity.Current;
        if (activity is not null)
        {
            var correlation = activity.GetBaggageItem(LogPropertyNames.CorrelationBaggageKey);
            if (!string.IsNullOrEmpty(correlation))
                return correlation;

            var newId = Ulid.NewUlid().ToString(null, CultureInfo.InvariantCulture);
            activity.SetBaggage(LogPropertyNames.CorrelationBaggageKey, newId);
            return newId;
        }

        return Ulid.NewUlid().ToString(null, CultureInfo.InvariantCulture);
    }

    private async Task FlushLoopAsync()
    {
        var batch = new List<LogEvent>(_batchSize);
        var reader = _channel.Reader;

        try
        {
            while (true)
            {
                batch.Clear();

                if (!await reader.WaitToReadAsync().ConfigureAwait(false))
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

                if (batch.Count > 0)
                {
                    try
                    {
                        await SendBatchAsync(BuildBatch(batch)).ConfigureAwait(false);
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

    private LogEventBatch BuildBatch(List<LogEvent> events)
    {
        var batch = new LogEventBatch { Domain = _domain };
        foreach (var logEvent in events)
            batch.Events.Add(logEvent);
        return batch;
    }

    private async Task SendBatchAsync(LogEventBatch batch)
    {
        if (IsCircuitOpen)
        {
            BufferCriticalEvents(batch);
            return;
        }

        try
        {
            FlushCriticalBuffer(batch);

            var stream = await LogEventBatchSerializer.ToStream(batch).ConfigureAwait(false);
            using (stream)
            {
                using var content = new StreamContent(stream);
                content.Headers.ContentType = new MediaTypeHeaderValue(LogEventBatchSerializer.ContentType);
                content.Headers.Add("X-Domain", _domain);

                var response = await _client.PostAsync("api/sink", content, CancellationToken.None).ConfigureAwait(false);
                response.EnsureSuccessStatusCode();

                OnSuccess();
            }
        }
        catch
        {
            OnFailure(batch);
        }
    }

    private void OnSuccess()
    {
        lock (_circuitLock)
        {
            _consecutiveFailures = 0;
            _circuitOpenUntil = DateTime.MinValue;
        }
    }

    private void OnFailure(LogEventBatch batch)
    {
        lock (_circuitLock)
        {
            _consecutiveFailures++;

            if (_consecutiveFailures >= FailureThreshold)
            {
                var backoffFactor = Math.Pow(2, _consecutiveFailures - FailureThreshold);
                var delay = TimeSpan.FromTicks((long)(BaseRetryDelay.Ticks * backoffFactor));

                if (delay > MaxRetryDelay)
                    delay = MaxRetryDelay;

                _circuitOpenUntil = DateTime.UtcNow.Add(delay);
            }
        }

        BufferCriticalEvents(batch);
    }

    private void BufferCriticalEvents(LogEventBatch batch)
    {
        foreach (var logEvent in batch.Events)
        {
            if (logEvent.Level >= (int)LogLevel.Warning)
            {
                while (_criticalBuffer.Count >= MaxCriticalBufferSize)
                {
                    if (_criticalBuffer.TryDequeue(out _))
                        Interlocked.Increment(ref _droppedEventCount);
                }

                _criticalBuffer.Enqueue(logEvent);
            }
        }
    }

    private void FlushCriticalBuffer(LogEventBatch batch)
    {
        var buffered = new List<LogEvent>();
        while (_criticalBuffer.TryDequeue(out var logEvent))
            buffered.Add(logEvent);

        for (var i = 0; i < buffered.Count; i++)
            batch.Events.Insert(i, buffered[i]);
    }

    /// <summary>
    /// Completes the channel, waits for pending batches to flush, and disposes or stops the switch source.
    /// </summary>
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

        if (_ownsDependencies)
        {
            if (_switchSource is IAsyncDisposable asyncDisposable)
                await asyncDisposable.DisposeAsync().ConfigureAwait(false);
            else
                _switchSource.Dispose();

            _client.Dispose();
        }
        else
        {
            _switchSource.Stop();
        }
    }
}
