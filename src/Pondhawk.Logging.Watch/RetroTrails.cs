// Copyright (c) Pond Hawk Technologies Inc. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System.Diagnostics;

namespace Pondhawk.Logging.Watch;

/// <summary>
/// The kept Debug trails of units of work marked for retro logging. Each trail is a ring of at most
/// <see cref="RetroOptions.MaxEventsPerUnit"/> events, held as a custom property on the marked
/// <see cref="Activity"/> itself: it lives exactly as long as the unit of work, needs no table, cap or sweep, and is
/// collected with the <see cref="Activity"/> when the unit of work succeeds. Nothing here throws.
/// </summary>
internal sealed class RetroTrails
{
    // Watch's own property, beside the mark; not part of the contract with the API.
    private const string TrailProperty = "Pondhawk.RetroLogging.Trail";

    // Guards the first event of a unit of work (create and set the trail) and its release: rare, and short.
    private static readonly Lock TrailLock = new();

    private readonly int _maxEvents;
    private long _dropped;

    public RetroTrails(RetroOptions options) =>
        _maxEvents = Math.Clamp(options.MaxEventsPerUnit, 1, 10_000);

    /// <summary>Gets the number of events dropped: the oldest of a full trail, or one added as its trail was sent.</summary>
    public long DroppedCount => Interlocked.Read(ref _dropped);

    /// <summary>Keeps <paramref name="logEvent"/> in the trail of the <paramref name="marked"/> unit of work.</summary>
    public void Hold(Activity marked, LogEvent logEvent)
    {
        try
        {
            if (marked.GetCustomProperty(TrailProperty) is not Trail trail)
            {
                lock (TrailLock)
                {
                    if (marked.GetCustomProperty(TrailProperty) is not Trail existing)
                    {
                        existing = new Trail();
                        marked.SetCustomProperty(TrailProperty, existing);
                    }

                    trail = existing;
                }
            }

            if (!trail.Add(logEvent, _maxEvents))
                Interlocked.Increment(ref _dropped);
        }
        catch
        {
            // The trail is a bonus; a failure to keep it never reaches the logging call.
        }
    }

    /// <summary>
    /// Removes the trail of the <paramref name="marked"/> unit of work and returns its events, oldest first; empty when
    /// it has none. A later event starts a new trail.
    /// </summary>
    public static IReadOnlyList<LogEvent> Release(Activity marked)
    {
        try
        {
            Trail? trail;
            lock (TrailLock)
            {
                trail = marked.GetCustomProperty(TrailProperty) as Trail;
                if (trail is null)
                    return [];

                marked.SetCustomProperty(TrailProperty, null);
            }

            return trail.Close();
        }
        catch
        {
            return [];
        }
    }

    private sealed class Trail
    {
        private readonly Lock _lock = new();
        private readonly Queue<LogEvent> _events = new();
        private bool _closed;

        // False when an event was dropped: the oldest to make room, or this one because the trail was just sent.
        public bool Add(LogEvent logEvent, int max)
        {
            lock (_lock)
            {
                if (_closed)
                    return false;

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
