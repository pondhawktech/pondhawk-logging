// Copyright (c) Pond Hawk Technologies Inc. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using Microsoft.Extensions.Logging;
using Pondhawk.Context;
using ZLogger;

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

    /// <summary>
    /// Copies <paramref name="entry"/> out, on the calling thread: the pooled entry goes back to ZLogger once
    /// the processor's <c>Post</c> returns, and the correlation id, subject and tenant are the ambient
    /// <see cref="IOperationContext"/>'s only there.
    /// </summary>
    public static CapturedEvent From(IZLoggerEntry entry)
    {
        var info = entry.LogInfo;
        var operation = Ambient<IOperationContext>.Current;

        var logEvent = new CapturedEvent
        {
            Level = info.LogLevel,
            Category = info.Category.Name,
            Title = entry.ToString(),
            CorrelationId = operation.CorrelationId,
            Subject = operation.SubjectName,
            Tenant = operation.Tenant,
            Occurred = info.Timestamp.Utc.UtcDateTime,
            Exception = info.Exception,
        };

        for (var i = 0; i < entry.ParameterCount; i++)
        {
            var key = entry.GetParameterKeyAsString(i);
            var value = entry.GetParameterValue(i);

            switch (key)
            {
                case LogStateContract.Nesting when value is int nesting:
                    logEvent.Nesting = nesting;
                    break;
                case LogStateContract.PayloadType when value is int payloadType:
                    logEvent.PayloadType = payloadType;
                    break;
                case LogStateContract.PayloadContent:
                    logEvent.Payload = value as string ?? value?.ToString();
                    break;
                case LogStateContract.ErrorContext:
                    logEvent.ErrorContext = value as string ?? value?.ToString();
                    break;
            }
        }

        return logEvent;
    }
}
