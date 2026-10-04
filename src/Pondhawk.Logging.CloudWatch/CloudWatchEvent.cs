// Copyright (c) Pond Hawk Technologies Inc. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using Microsoft.Extensions.Logging;

namespace Pondhawk.Logging.CloudWatch;

/// <summary>
/// What the processor copies out of a pooled ZLogger entry on the calling thread: everything
/// <see cref="CloudWatchEventFormatter"/> needs to write the event's JSON later, on the flush thread.
/// </summary>
internal sealed class CloudWatchEvent
{
    public LogLevel Level { get; init; }

    public string Category { get; init; } = string.Empty;

    public string Title { get; init; } = string.Empty;

    public string? CorrelationId { get; init; }

    public DateTime Occurred { get; init; }

    /// <summary>The method-tracing delta: +1 on entry, -1 on exit, 0 for every other event.</summary>
    public int Nesting { get; set; }

    public PayloadType PayloadType { get; set; }

    public string? Payload { get; set; }

    /// <summary>The <c>ErrorWithContext</c> context object, already serialized to JSON by the logging API.</summary>
    public string? ErrorContext { get; set; }

    public Exception? Exception { get; init; }
}
