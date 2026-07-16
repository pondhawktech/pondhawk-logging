// Copyright (c) Pond Hawk Technologies Inc. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System.Drawing;
using Serilog.Events;

namespace Pondhawk.Logging.Watch;

/// <summary>
/// Default implementation of ISwitch with fluent configuration API.
/// </summary>
/// <remarks>
/// Switch instances are immutable after construction via the fluent API.
/// They are safe to cache and share across threads.
/// </remarks>
public class Switch
{
    /// <summary>
    /// Creates a new Switch instance with default values.
    /// </summary>
    /// <returns>A new Switch for fluent configuration.</returns>
    public static Switch Create()
    {
        return new Switch();
    }

    /// <summary>
    /// Gets the pattern to match against logger categories.
    /// </summary>
    public string Pattern { get; init; } = "";

    /// <summary>
    /// Gets an optional tag for categorization.
    /// </summary>
    public string Tag { get; init; } = "";

    /// <summary>
    /// Gets the minimum log level.
    /// </summary>
    public LogEventLevel Level { get; init; } = LogEventLevel.Error;

    /// <summary>
    /// Gets the color for log events.
    /// </summary>
    public Color Color { get; init; } = Color.White;

}
