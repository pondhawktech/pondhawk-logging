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
