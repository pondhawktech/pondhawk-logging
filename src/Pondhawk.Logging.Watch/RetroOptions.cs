// Copyright (c) Pond Hawk Technologies Inc. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

namespace Pondhawk.Logging.Watch;

/// <summary>
/// The limits on retro logging (<see cref="WatchOptions.Retro"/>). A marked unit of work's trail is kept on its
/// <see cref="System.Diagnostics.Activity"/>, so it lives exactly as long as the unit of work; the only limit is its
/// length. The trail is a bonus, so the limit drops events rather than hold memory.
/// </summary>
public class RetroOptions
{
    /// <summary>
    /// Gets or sets how many events are kept per unit of work. The oldest is dropped first, so the events nearest the
    /// error stay. Default 100; clamped to 1–10,000.
    /// </summary>
    public int MaxEventsPerUnit { get; set; } = 100;

    /// <summary>Not used: a trail lives on its unit of work's <see cref="System.Diagnostics.Activity"/>, so there is no table to cap.</summary>
    [Obsolete("Not used since 5.0.2: a trail lives on its unit of work's Activity, so there is no table to cap.")]
    public int MaxUnits { get; set; } = 256;

    /// <summary>Not used: a trail goes with its unit of work's <see cref="System.Diagnostics.Activity"/>, so nothing waits to be swept.</summary>
    [Obsolete("Not used since 5.0.2: a trail goes with its unit of work's Activity, so nothing waits to be swept.")]
    public TimeSpan IdleTimeout { get; set; } = TimeSpan.FromMinutes(2);
}
