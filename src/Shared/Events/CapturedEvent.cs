// Copyright (c) Pond Hawk Technologies Inc. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using Microsoft.Extensions.Logging;

namespace Pondhawk.Logging.Events;

/// <summary>
/// What a processor copies out of a pooled ZLogger entry on the calling thread: everything
/// <see cref="EventShaper"/> needs to shape the event later, on whichever thread writes it.
/// </summary>
internal sealed class CapturedEvent
{
    public LogLevel Level { get; init; }

    public string Category { get; init; } = string.Empty;

    public string Title { get; init; } = string.Empty;

    public string? CorrelationId { get; init; }

    /// <summary>Who the unit of work was for (<c>IOperationContext.SubjectName</c>).</summary>
    public string? Subject { get; init; }

    /// <summary>The tenant the unit of work belonged to (<c>IOperationContext.Tenant</c>).</summary>
    public string? Tenant { get; init; }

    public DateTime Occurred { get; init; }

    /// <summary>The method-tracing delta: +1 on entry, -1 on exit, 0 for every other event.</summary>
    public int Nesting { get; set; }

    /// <summary>The payload's type, as the API numbers it (<see cref="LogStateContract"/>).</summary>
    public int PayloadType { get; set; }

    public string? Payload { get; set; }

    /// <summary>The <c>ErrorWithContext</c> context object, already serialized to JSON by the logging API.</summary>
    public string? ErrorContext { get; set; }

    public Exception? Exception { get; init; }
}
