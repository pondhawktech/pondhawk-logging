// Copyright (c) Pond Hawk Technologies Inc. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System.Drawing;
using Serilog.Events;

namespace Pondhawk.Logging.Watch;

/// <summary>
/// Defines the configuration for a logging switch.
/// Used to configure switches via ISwitchSource.Update().
/// </summary>
/// <remarks>
/// SwitchDef is a mutable DTO used for configuration and serialization.
/// The ISwitchSource converts these to immutable ISwitch instances.
/// </remarks>
public class SwitchDef
{
    /// <summary>
    /// Gets or sets the pattern to match against logger categories.
    /// Uses prefix matching (longest match wins).
    /// </summary>
    public string Pattern { get; set; } = "";

    /// <summary>
    /// Gets or sets the filter type for advanced matching.
    /// Reserved for future use.
    /// </summary>
    public string FilterType { get; set; } = "";

    /// <summary>
    /// Gets or sets the filter target for advanced matching.
    /// Reserved for future use.
    /// </summary>
    public string FilterTarget { get; set; } = "";

    /// <summary>
    /// Gets or sets an optional tag for categorization.
    /// </summary>
    public string Tag { get; set; } = "";

    /// <summary>
    /// Gets or sets the minimum log level.
    /// </summary>
    public LogEventLevel Level { get; set; } = LogEventLevel.Error;

    /// <summary>
    /// Gets or sets the color for log events matching this switch.
    /// </summary>
    public Color Color { get; set; } = Color.LightGray;
}
