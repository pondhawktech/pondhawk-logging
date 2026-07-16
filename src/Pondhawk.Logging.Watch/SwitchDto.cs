// Copyright (c) Pond Hawk Technologies Inc. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

namespace Pondhawk.Logging.Watch;

/// <summary>
/// Data transfer object for switch configuration from Watch Server.
/// </summary>
/// <remarks>
/// Matches the JSON format returned by GET /api/switches?domain={domain}.
/// Uses PascalCase property names to match Watch Server's JSON serialization.
/// </remarks>
public class SwitchDto
{
    /// <summary>
    /// Gets or sets the pattern to match against category names.
    /// </summary>
    public string Pattern { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the optional tag.
    /// </summary>
    public string Tag { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the minimum log level.
    /// </summary>
    public int Level { get; set; }

    /// <summary>
    /// Gets or sets the ARGB color value.
    /// </summary>
    public int Color { get; set; }
}
