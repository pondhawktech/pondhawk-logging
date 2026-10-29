// Copyright (c) Pond Hawk Technologies Inc. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System.Text.Json;
using Microsoft.Extensions.Logging;

namespace Pondhawk.Logging.Events;

/// <summary>
/// One event as <see cref="EventShaper"/> shaped it: each part already capped and cut to fit, ready for a
/// writer to lay out — as one JSON object for CloudWatch, as one field per part for the journal. A writer
/// adds nothing and leaves nothing out; every provider that compiles this file shows the same content.
/// </summary>
internal sealed class EventParts
{
    public LogLevel Level { get; init; }

    public string Category { get; init; } = string.Empty;

    public string Title { get; init; } = string.Empty;

    /// <summary>The correlation id, or null when the unit of work had none.</summary>
    public string? CorrelationId { get; init; }

    /// <summary>Who the unit of work was for, or null when not set.</summary>
    public string? Subject { get; init; }

    /// <summary>The tenant, or null when not set.</summary>
    public string? Tenant { get; init; }

    /// <summary>The method-tracing delta: +1 on entry, -1 on exit, 0 for every other event.</summary>
    public int Nesting { get; init; }

    /// <summary>The context, always a JSON object; null when there is none or it was cut.</summary>
    public JsonElement? Context { get; init; }

    /// <summary>A JSON payload as nested JSON. Null when the payload is text, or there is none.</summary>
    public JsonElement? PayloadJson { get; init; }

    /// <summary>A text payload as its lines. Null when the payload is JSON, or there is none.</summary>
    public IReadOnlyList<string>? PayloadLines { get; init; }

    /// <summary>The exception chain, outermost first. Null when the event has no exception.</summary>
    public IReadOnlyList<ExceptionPart>? Exceptions { get; init; }

    /// <summary>
    /// The event could not be cut to fit and is only its level, category, title and outermost exception,
    /// each cut short.
    /// </summary>
    public bool Truncated { get; init; }
}
