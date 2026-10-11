// Copyright (c) Pond Hawk Technologies Inc. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using Microsoft.Extensions.Logging;

namespace Pondhawk.Logging.AspNetCore;

/// <summary>The events the diagnostics middlewares log, all under one category.</summary>
internal static partial class DiagnosticsLog
{
    /// <summary>
    /// The category of every event here. Deliberately under no application's namespace, so a standing switch on
    /// an application's own prefix does not turn the request dump on by accident — it takes its own switch.
    /// </summary>
    public const string Category = "Pondhawk.Diagnostics.Http";

    [LoggerMessage(EventId = 1, Level = LogLevel.Debug, Message = "Begin {Method} {Path}")]
    public static partial void Begin(ILogger logger, string method, string? path);

    [LoggerMessage(EventId = 2, Message = "End {Method} {Path} -> {Status} ({Elapsed:F1}ms)")]
    public static partial void End(ILogger logger, LogLevel level, Exception? exception, string method, string? path, int status, double elapsed);
}
