// Copyright (c) Pond Hawk Technologies Inc. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading.Channels;
using CommunityToolkit.Diagnostics;
using Pondhawk.Logging;
using Serilog.Core;
using Serilog.Events;
using SerilogEvent = Serilog.Events.LogEvent;

namespace Pondhawk.Logging.Watch;

/// <summary>
/// Serilog ILogEventSink with Channel-based batching, HTTP posting, and circuit breaker
/// for the Watch pipeline.
/// </summary>
/// <remarks>
/// <para>
/// Combines Channel-based batching with Serilog-to-Watch event mapping and
/// HTTP posting with circuit breaker resilience.
/// </para>
/// <para>
/// Emit() is non-blocking: events are written to an unbounded channel.
/// A background task drains the channel by batch size or flush interval
/// and sends converted Watch LogEvents to the Watch Server.
/// </para>
/// </remarks>
public sealed class WatchSink : ILogEventSink, IDisposable, IAsyncDisposable
{
    private readonly HttpClient _client;
    private readonly SwitchSource _switchSource;
    private readonly bool _ownsDependencies;
    private readonly string _domain;
    private readonly int _batchSize;
    private readonly TimeSpan _flushInterval;

    private readonly Channel<SerilogEvent> _channel;
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

    /// <summary>
    /// Gets or sets the failure threshold before the circuit opens.
    /// </summary>
    public int FailureThreshold { get; set; } = 3;

    /// <summary>
    /// Gets or sets the base delay before retrying after circuit opens.
    /// </summary>
    public TimeSpan BaseRetryDelay { get; set; } = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Gets or sets the maximum retry delay.
    /// </summary>
    public TimeSpan MaxRetryDelay { get; set; } = TimeSpan.FromMinutes(2);

    /// <summary>
    /// Gets or sets the maximum number of critical events to buffer during outage.
    /// </summary>
    public int MaxCriticalBufferSize { get; set; } = 1000;

    /// <summary>
    /// Gets the current circuit state.
    /// </summary>
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

    /// <summary>
    /// Gets the number of events currently in the critical buffer.
    /// </summary>
    public int CriticalBufferCount => _criticalBuffer.Count;

    /// <summary>
    /// Gets the total number of events dropped due to buffer overflow.
    /// </summary>
    public long DroppedEventCount => Interlocked.Read(ref _droppedEventCount);

    /// <summary>
    /// Initializes a new instance of the <see cref="WatchSink"/> class.
    /// </summary>
    /// <param name="client">The <see cref="HttpClient"/> used to post event batches to the Watch Server.</param>
    /// <param name="switchSource">The switch source for dynamic log level filtering.</param>
    /// <param name="domain">The domain name included in each batch. Defaults to "Default".</param>
    /// <param name="batchSize">Maximum events per batch before flushing. Defaults to 100.</param>
    /// <param name="flushInterval">Maximum time before flushing a partial batch. Defaults to 100ms.</param>
    /// <param name="ownsDependencies">
    /// When <see langword="true"/>, the sink disposes <paramref name="switchSource"/> and
    /// <paramref name="client"/> on disposal (they were created for it). When <see langword="false"/>
    /// (the default, for the low-level API where the caller supplies these), the sink only stops the
    /// switch source and leaves both for the caller to dispose.
    /// </param>
    public WatchSink(
        HttpClient client,
        SwitchSource switchSource,
        string domain = "Default",
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

        _channel = Channel.CreateUnbounded<SerilogEvent>(new UnboundedChannelOptions
        {
            SingleWriter = false,
            SingleReader = true
        });

        _flushTask = Task.Run(FlushLoopAsync);
    }

    /// <summary>
    /// Emits a Serilog log event into the channel for batched processing.
    /// </summary>
    public void Emit(SerilogEvent logEvent)
    {
        if (Volatile.Read(ref _disposed) != 0)
            return;

        // Capture correlation ID on the caller's thread where Activity.Current is available.
        // The flush loop runs on a background thread that has no access to the caller's Activity.
        var correlationId = GetCorrelationId();
        logEvent.AddPropertyIfAbsent(new LogEventProperty(LogPropertyNames.CorrelationId, new ScalarValue(correlationId)));

        _channel.Writer.TryWrite(logEvent);
    }

    private async Task FlushLoopAsync()
    {
        var batch = new List<SerilogEvent>(_batchSize);
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
                    // Timeout expired, flush what we have
                }

                if (batch.Count > 0)
                {
                    try
                    {
                        await FlushBatchAsync(batch).ConfigureAwait(false);
                    }
                    catch
                    {
                        // Backstop: the drain loop must outlive any single batch failure.
                        // Losing one batch is acceptable; silently killing all delivery is not.
                    }
                }
            }
        }
        finally
        {
            _flushCompleted.TrySetResult(true);
        }
    }

    internal async Task FlushBatchAsync(List<SerilogEvent> events)
    {
        var watchBatch = new LogEventBatch { Domain = _domain };

        foreach (var serilogEvent in events)
        {
            LogEvent? converted;
            try
            {
                converted = ConvertEvent(serilogEvent);
            }
            catch
            {
                // A single malformed event (e.g. a property whose ToString throws) must never
                // abort the batch — skip it rather than lose everything behind it.
                continue;
            }

            if (converted is not null)
                watchBatch.Events.Add(converted);
        }

        if (watchBatch.Events.Count > 0)
        {
            await SendBatchAsync(watchBatch).ConfigureAwait(false);
        }
    }

    private async Task SendBatchAsync(LogEventBatch batch)
    {
        // Check circuit state
        if (IsCircuitOpen)
        {
            BufferCriticalEvents(batch);
            return;
        }

        try
        {
            // Add any buffered critical events to this batch
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
                // Calculate delay with exponential backoff
                var backoffFactor = Math.Pow(2, _consecutiveFailures - FailureThreshold);
                var delay = TimeSpan.FromTicks((long)(BaseRetryDelay.Ticks * backoffFactor));

                if (delay > MaxRetryDelay)
                    delay = MaxRetryDelay;

                _circuitOpenUntil = DateTime.UtcNow.Add(delay);
            }
        }

        // Buffer critical events from the failed batch
        BufferCriticalEvents(batch);
    }

    private void BufferCriticalEvents(LogEventBatch batch)
    {
        foreach (var e in batch.Events)
        {
            // Buffer Warning and Error level events
            if (e.Level >= (int)LogEventLevel.Warning)
            {
                // Enforce max buffer size - track dropped events
                while (_criticalBuffer.Count >= MaxCriticalBufferSize)
                {
                    if (_criticalBuffer.TryDequeue(out _))
                    {
                        Interlocked.Increment(ref _droppedEventCount);
                    }
                }

                _criticalBuffer.Enqueue(e);
            }
        }
    }

    private void FlushCriticalBuffer(LogEventBatch batch)
    {
        var buffered = new List<LogEvent>();
        while (_criticalBuffer.TryDequeue(out var e))
        {
            buffered.Add(e);
        }

        for (var i = 0; i < buffered.Count; i++)
            batch.Events.Insert(i, buffered[i]);
    }

    private LogEvent? ConvertEvent(SerilogEvent serilogEvent)
    {
        var category = GetCategory(serilogEvent);
        var sw = _switchSource.Lookup(category);

        if (serilogEvent.Level < sw.Level)
            return null;

        var le = new LogEvent
        {
            Category = category,
            Level = (int)serilogEvent.Level,
            Color = sw.Color.ToArgb(),
            Tag = sw.Tag,
            Title = serilogEvent.RenderMessage(System.Globalization.CultureInfo.InvariantCulture),
            CorrelationId = GetCorrelationIdFromEvent(serilogEvent),
            Occurred = serilogEvent.Timestamp.UtcDateTime
        };

        // Extract Watch-specific properties
        if (serilogEvent.Properties.TryGetValue(LogPropertyNames.Nesting, out var nestingValue) &&
            nestingValue is ScalarValue { Value: int nesting })
        {
            le.Nesting = nesting;
        }

        if (serilogEvent.Properties.TryGetValue(LogPropertyNames.PayloadType, out var typeValue) &&
            typeValue is ScalarValue { Value: int payloadType } &&
            serilogEvent.Properties.TryGetValue(LogPropertyNames.PayloadContent, out var contentValue) &&
            contentValue is ScalarValue { Value: string payloadContent })
        {
            le.Type = payloadType;
            le.Payload = payloadContent;
        }
        else if (serilogEvent.Exception is not null)
        {
            var exception = serilogEvent.Exception;
            le.ErrorType = exception.GetType().FullName ?? exception.GetType().Name;

            // Transmit the full exception detail (message + stack trace) on the wire. The
            // ignored Error member is an in-process staging field; the type name alone is not
            // enough to diagnose a failure, so carry ToString() as a text payload.
            le.Type = (int)PayloadType.Text;
            le.Payload = exception.ToString();
        }
        else
        {
            var payload = BuildStructuredPayload(serilogEvent);
            if (payload is not null)
            {
                le.Type = (int)PayloadType.Json;
                le.Payload = payload;
            }
        }

        return le;
    }

    private static string GetCategory(SerilogEvent logEvent)
    {
        if (logEvent.Properties.TryGetValue("SourceContext", out var sourceContext) &&
            sourceContext is ScalarValue { Value: string category })
        {
            return category;
        }

        return "Serilog";
    }

    private static string GetCorrelationIdFromEvent(SerilogEvent logEvent)
    {
        if (logEvent.Properties.TryGetValue(LogPropertyNames.CorrelationId, out var value) &&
            value is ScalarValue { Value: string correlationId })
        {
            return correlationId;
        }

        return Ulid.NewUlid().ToString(null, System.Globalization.CultureInfo.InvariantCulture);
    }

    private static string GetCorrelationId()
    {
        var activity = Activity.Current;
        if (activity != null)
        {
            var correlation = activity.GetBaggageItem(LogPropertyNames.CorrelationBaggageKey);
            if (!string.IsNullOrEmpty(correlation))
                return correlation!;

            var newId = Ulid.NewUlid().ToString(null, System.Globalization.CultureInfo.InvariantCulture);
            activity.SetBaggage(LogPropertyNames.CorrelationBaggageKey, newId);
            return newId;
        }

        return Ulid.NewUlid().ToString(null, System.Globalization.CultureInfo.InvariantCulture);
    }

    private static string? BuildStructuredPayload(SerilogEvent logEvent)
    {
        var properties = logEvent.Properties
            .Where(p => !string.Equals(p.Key, "SourceContext", StringComparison.Ordinal) &&
                        !p.Key.StartsWith(LogPropertyNames.Prefix, StringComparison.Ordinal))
            .ToList();

        if (properties.Count == 0)
            return null;

        try
        {
            var dict = new Dictionary<string, object?>(StringComparer.Ordinal);
            foreach (var prop in properties)
            {
                dict[prop.Key] = ConvertPropertyValue(prop.Value);
            }

            return JsonSerializer.Serialize(dict);
        }
        catch
        {
            return null;
        }
    }

    private static object? ConvertPropertyValue(LogEventPropertyValue value)
    {
        return value switch
        {
            ScalarValue sv => sv.Value,
            SequenceValue seq => seq.Elements.Select(ConvertPropertyValue).ToList(),
            StructureValue str => str.Properties.ToDictionary(p => p.Name, p => ConvertPropertyValue(p.Value), StringComparer.Ordinal),
            DictionaryValue dv => dv.Elements.ToDictionary(
                kvp => ConvertPropertyValue(kvp.Key)?.ToString() ?? "",
                kvp => ConvertPropertyValue(kvp.Value),
                StringComparer.Ordinal),
            _ => value.ToString(null, System.Globalization.CultureInfo.InvariantCulture)
        };
    }

    /// <summary>
    /// Completes the channel, waits for pending batches to flush, and stops the switch source.
    /// </summary>
    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
            return;

        _channel.Writer.Complete();

        try
        {
            _flushCompleted.Task.Wait(TimeSpan.FromSeconds(10));
        }
        catch
        {
            // Ignore exceptions during disposal
        }

        if (_ownsDependencies)
        {
            _switchSource.Dispose();
            _client.Dispose();
        }
        else
        {
            _switchSource.Stop();
        }
    }

    /// <summary>
    /// Asynchronously completes the channel, waits for pending batches to flush, and stops the switch source.
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
            // Ignore exceptions during disposal (including timeout)
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
