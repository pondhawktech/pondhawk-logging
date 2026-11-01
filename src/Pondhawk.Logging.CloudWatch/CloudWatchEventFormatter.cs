// Copyright (c) Pond Hawk Technologies Inc. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System.Text;
using System.Text.Json;

namespace Pondhawk.Logging.CloudWatch;

/// <summary>
/// Writes one event as the JSON object stored in CloudWatch Logs, so Logs Insights can filter on its
/// fields — <c>Level</c>, <c>Category</c>, <c>CorrelationId</c>, <c>Subject</c>, <c>Tenant</c>, <c>Context</c> — without parsing text.
/// </summary>
/// <remarks>
/// <para>
/// What the event holds, and how it is cut to fit, is <see cref="EventShaper"/>'s; this lays the parts out.
/// A JSON payload is written under <c>Payload</c> as nested JSON, never as JSON inside a string; a text
/// payload is a string, or an array of lines if it has several. <c>Truncated</c> is written on an event of
/// which anything was cut (<see cref="EventParts.Cut"/>), as the journal sink's <c>TRUNCATED</c> is.
/// </para>
/// <para>
/// <see cref="Format"/> never throws and always returns: it runs on the provider's one flush thread, where
/// an event that threw would cost its whole batch and one that looped would stop CloudWatch for the
/// process.
/// </para>
/// </remarks>
internal static class CloudWatchEventFormatter
{
    /// <summary>One event's message is kept under this; a longer one is cut.</summary>
    public const int MaxEventBytes = 256 * 1024;

    /// <summary>Formats <paramref name="logEvent"/> as its CloudWatch JSON, under <see cref="MaxEventBytes"/>.</summary>
    public static string Format(CapturedEvent logEvent) =>
        EventShaper.Shape(logEvent, MaxEventBytes, Write, static message => Encoding.UTF8.GetByteCount(message));

    private static string Write(EventParts parts)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("Level", parts.Level.ToString());
            writer.WriteString("Category", parts.Category);
            if (parts.CorrelationId is not null)
                writer.WriteString("CorrelationId", parts.CorrelationId);

            // Who the work was for and which tenant: present only when the unit of work set them.
            if (parts.Subject is not null)
                writer.WriteString("Subject", parts.Subject);
            if (parts.Tenant is not null)
                writer.WriteString("Tenant", parts.Tenant);

            writer.WriteString("Title", parts.Title);
            if (parts.Cut)
                writer.WriteBoolean("Truncated", value: true);

            // A delta, present only on the method-tracing events that carry one.
            if (parts.Nesting != 0)
                writer.WriteNumber("Nesting", parts.Nesting);

            if (parts.Context is { } context)
            {
                writer.WritePropertyName("Context");
                context.WriteTo(writer);
            }

            if (parts.Exceptions is not null)
            {
                WriteExceptions(writer, parts.Exceptions);
            }
            else if (parts.PayloadJson is { } json)
            {
                writer.WritePropertyName("Payload");
                json.WriteTo(writer);
            }
            else if (parts.PayloadLines is { Count: 1 } line)
            {
                writer.WriteString("Payload", line[0]);
            }
            else if (parts.PayloadLines is { } lines)
            {
                writer.WriteStartArray("Payload");
                foreach (var l in lines)
                    writer.WriteStringValue(l);
                writer.WriteEndArray();
            }

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    // One frame per array entry, so the console shows the stack trace line by line.
    private static void WriteExceptions(Utf8JsonWriter writer, IReadOnlyList<ExceptionPart> exceptions)
    {
        writer.WriteStartArray("Exceptions");
        foreach (var exception in exceptions)
        {
            writer.WriteStartObject();
            writer.WriteString("Type", exception.Type);
            writer.WriteString("Message", exception.Message);
            writer.WriteStartArray("StackTrace");
            foreach (var frame in exception.StackTrace)
                writer.WriteStringValue(frame);
            writer.WriteEndArray();
            writer.WriteEndObject();
        }

        writer.WriteEndArray();
    }
}
