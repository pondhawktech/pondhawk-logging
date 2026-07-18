// Copyright (c) Pond Hawk Technologies Inc. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System.Drawing;
using Microsoft.Extensions.Logging;

namespace Pondhawk.Logging.Watch;

/// <summary>
/// Configuration options for <see cref="WatchLoggingBuilderExtensions"/>'s <c>AddWatch</c>.
/// </summary>
public class WatchSinkOptions
{
    /// <summary>
    /// Gets or sets the Watch Server URL. Default is "http://localhost:11000".
    /// </summary>
    public string ServerUrl { get; set; } = "http://localhost:11000";

    /// <summary>
    /// Gets or sets the domain name for log event batches. Default is "Default".
    /// </summary>
    public string Domain { get; set; } = "Default";

    /// <summary>
    /// Gets or sets the default log level when no switch pattern matches. Default is Warning.
    /// </summary>
    public LogLevel DefaultLevel { get; set; } = LogLevel.Warning;

    /// <summary>
    /// Gets or sets the default color when no switch pattern matches. Default is LightGray.
    /// </summary>
    public Color DefaultColor { get; set; } = Color.LightGray;

    /// <summary>
    /// Gets or sets the batch size before flushing. Default is 100.
    /// </summary>
    public int BatchSize { get; set; } = 100;

    /// <summary>
    /// Gets or sets the flush interval when batch size is not reached. Null uses the sink default.
    /// </summary>
    public TimeSpan? FlushInterval { get; set; }

    /// <summary>
    /// Gets or sets the switch polling interval. Null uses the WatchSwitchSource default (30 seconds).
    /// </summary>
    public TimeSpan? PollInterval { get; set; }
}
