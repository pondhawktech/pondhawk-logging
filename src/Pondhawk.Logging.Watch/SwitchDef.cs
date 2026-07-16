/*
The MIT License (MIT)

Copyright (c) 2017 The Kampilan Group Inc.
Copyright (c) 2025 Pond Hawk Technologies Inc.

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
*/

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
