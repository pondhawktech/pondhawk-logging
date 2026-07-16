// Copyright (c) Pond Hawk Technologies Inc. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

namespace Pondhawk.Logging.Watch;

/// <summary>
/// Response from the switches API endpoint.
/// </summary>
public class SwitchesResponse
{
    /// <summary>
    /// Gets or sets the list of switch definitions.
    /// </summary>
    public IList<SwitchDto> Switches { get; set; } = [];
}
