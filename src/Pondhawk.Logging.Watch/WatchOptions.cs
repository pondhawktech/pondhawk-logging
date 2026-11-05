// Copyright (c) Pond Hawk Technologies Inc. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System.Drawing;
using Microsoft.Extensions.Logging;

namespace Pondhawk.Logging.Watch;

/// <summary>
/// Configuration options for <see cref="WatchLoggingBuilderExtensions"/>'s <c>AddWatch</c>.
/// </summary>
public class WatchOptions
{
    /// <summary>
    /// Gets or sets the Watch URL — <c>scheme://[key@]host[:port][/base-path]/&lt;domain&gt;</c> — the
    /// provider delivers to. The last path segment is the domain; the optional user-info is a Watch API key.
    /// </summary>
    public string Url { get; set; } = string.Empty;

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
    /// Gets or sets the switch polling interval. Null uses the WatchSwitchSource default (5 seconds).
    /// Polls are conditional (If-None-Match), so an unchanged interval is a cheap 304 with no rebuild.
    /// </summary>
    public TimeSpan? PollInterval { get; set; }

    /// <summary>
    /// Gets or sets a function that gives the tag for events that neither a diagnostic probe nor their matching
    /// switch tags — for example the instance, build and start time, so the instances logging into one domain can
    /// be told apart. Null (the default) leaves those events untagged.
    /// </summary>
    /// <remarks>
    /// Called once, on the background flush thread, when the first batch is sent — never on a logging call. It
    /// must be able to give its value by then. A function that throws, or returns null or empty, leaves the tag
    /// empty for the life of the process; it is not called again.
    /// </remarks>
    public Func<string?>? DefaultTag { get; set; }
}
