// Copyright (c) Pond Hawk Technologies Inc. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

namespace Pondhawk.Logging.Watch;

/// <summary>
/// The limits on retro logging (<see cref="WatchOptions.Retro"/>): how much of a marked unit of work's dropped Debug
/// trail is kept, for how many units of work, and for how long. The trail is a bonus, so the limits drop events
/// rather than hold memory. Values out of range are clamped.
/// </summary>
public class RetroOptions
{
    /// <summary>
    /// Gets or sets how many events are kept per unit of work. The oldest is dropped first, so the events nearest the
    /// error stay. Default 100; clamped to 1–10,000.
    /// </summary>
    public int MaxEventsPerUnit { get; set; } = 100;

    /// <summary>
    /// Gets or sets how many units of work keep a trail at once. At the cap a new unit of work keeps nothing. Default
    /// 256; clamped to 1–100,000.
    /// </summary>
    public int MaxUnits { get; set; } = 256;

    /// <summary>
    /// Gets or sets how long a trail is kept after its last event. Watch has no signal for the end of a unit of work,
    /// so a trail not added to for this long is discarded. Default 2 minutes; clamped to 1 second–1 hour.
    /// </summary>
    public TimeSpan IdleTimeout { get; set; } = TimeSpan.FromMinutes(2);
}
