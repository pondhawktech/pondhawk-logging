// Copyright (c) Pond Hawk Technologies Inc. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System.Collections.Concurrent;

namespace Pondhawk.Logging.Watch;

/// <summary>
/// The kept Debug trails of units of work marked for retro logging, by correlation id. Each trail is a ring of at
/// most <see cref="RetroOptions.MaxEventsPerUnit"/> events; at most <see cref="RetroOptions.MaxUnits"/> trails exist
/// at once; a timer discards trails idle for <see cref="RetroOptions.IdleTimeout"/>. Nothing here throws.
/// </summary>
internal sealed class RetroBuffers : IDisposable
{
    private readonly ConcurrentDictionary<string, Trail> _trails = new(StringComparer.Ordinal);
    private readonly int _maxEvents;
    private readonly int _maxUnits;
    private readonly long _idleMs;
    private readonly Timer _sweeper;
    private int _count;
    private long _dropped;

    public RetroBuffers(RetroOptions options)
    {
        _maxEvents = Math.Clamp(options.MaxEventsPerUnit, 1, 10_000);
        _maxUnits = Math.Clamp(options.MaxUnits, 1, 100_000);
        var idle = TimeSpan.FromTicks(Math.Clamp(options.IdleTimeout.Ticks, TimeSpan.TicksPerSecond, TimeSpan.TicksPerHour));
        _idleMs = (long)idle.TotalMilliseconds;

        var period = TimeSpan.FromTicks(Math.Max(idle.Ticks / 2, TimeSpan.TicksPerSecond));
        _sweeper = new Timer(_ => Sweep(Environment.TickCount64), null, period, period);
    }

    /// <summary>Gets the number of events dropped: by a full trail, a full table, or an idle trail discarded.</summary>
    public long DroppedCount => Interlocked.Read(ref _dropped);

    /// <summary>Gets the number of trails kept now.</summary>
    public int Count => Volatile.Read(ref _count);

    /// <summary>Keeps <paramref name="logEvent"/> in the trail for <paramref name="correlationId"/>.</summary>
    public void Hold(string correlationId, LogEvent logEvent)
    {
        try
        {
            if (!_trails.TryGetValue(correlationId, out var trail))
            {
                if (Volatile.Read(ref _count) >= _maxUnits)
                {
                    Interlocked.Increment(ref _dropped);
                    return;
                }

                var fresh = new Trail();
                if (_trails.TryAdd(correlationId, fresh))
                {
                    Interlocked.Increment(ref _count);
                    trail = fresh;
                }
                else if (!_trails.TryGetValue(correlationId, out trail))
                {
                    Interlocked.Increment(ref _dropped);
                    return;
                }
            }

            if (!trail.Add(logEvent, _maxEvents, Environment.TickCount64))
                Interlocked.Increment(ref _dropped);
        }
        catch
        {
            // The trail is a bonus; a failure to keep it never reaches the logging call.
        }
    }

    /// <summary>
    /// Removes the trail for <paramref name="correlationId"/> and returns its events, oldest first; empty when there
    /// is none.
    /// </summary>
    public IReadOnlyList<LogEvent> Release(string correlationId)
    {
        if (!_trails.TryRemove(correlationId, out var trail))
            return [];

        Interlocked.Decrement(ref _count);
        return trail.Close();
    }

    /// <summary>Discards the trails idle since before <paramref name="nowMs"/> less the idle timeout.</summary>
    internal void Sweep(long nowMs)
    {
        try
        {
            foreach (var (id, trail) in _trails)
            {
                if (nowMs - trail.LastTouched < _idleMs)
                    continue;

                if (_trails.TryRemove(id, out var removed))
                {
                    Interlocked.Decrement(ref _count);
                    Interlocked.Add(ref _dropped, removed.Close().Count);
                }
            }
        }
        catch
        {
            // The sweep runs on a timer thread; a throw there would end the process.
        }
    }

    public void Dispose()
    {
        _sweeper.Dispose();
        _trails.Clear();
        Volatile.Write(ref _count, 0);
    }

    private sealed class Trail
    {
        private readonly Lock _lock = new();
        private readonly Queue<LogEvent> _events = new();
        private bool _closed;
        private long _lastTouched = Environment.TickCount64;

        public long LastTouched => Interlocked.Read(ref _lastTouched);

        // False when an event was dropped: the oldest to make room, or this one because the trail was just released.
        public bool Add(LogEvent logEvent, int max, long nowMs)
        {
            lock (_lock)
            {
                if (_closed)
                    return false;

                Interlocked.Exchange(ref _lastTouched, nowMs);
                _events.Enqueue(logEvent);
                if (_events.Count <= max)
                    return true;

                _events.Dequeue();
                return false;
            }
        }

        public List<LogEvent> Close()
        {
            lock (_lock)
            {
                _closed = true;
                var events = _events.ToList();
                _events.Clear();
                return events;
            }
        }
    }
}
