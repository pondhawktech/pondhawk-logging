// Copyright (c) Pond Hawk Technologies Inc. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System.Text;
using System.Text.Json;

namespace Pondhawk.Logging.CloudWatch;

/// <summary>
/// Writes one event as the JSON object stored in CloudWatch Logs, so Logs Insights can filter on its
/// fields — <c>Level</c>, <c>Category</c>, <c>CorrelationId</c>, <c>Context</c> — without parsing text.
/// </summary>
/// <remarks>
/// <para>
/// The shape is decided by what the event says it carries, never by guessing at the text:
/// </para>
/// <list type="bullet">
/// <item>an exception gives <c>Exceptions</c> — the whole chain, outermost first, each with its
/// <c>Type</c>, <c>Message</c> and a <c>StackTrace</c> of one frame per array entry — plus <c>Context</c>
/// from the object <c>ErrorWithContext</c> passed with it;</item>
/// <item>a payload typed as JSON is written under <c>Payload</c> as nested JSON, never as JSON inside a
/// string, and stays text only if it does not parse;</item>
/// <item>any other payload is text, or an array of lines if it has several.</item>
/// </list>
/// <para>
/// <c>Context</c> is always a JSON object, so <c>Context.Name</c> can always be queried: a context that
/// serializes to anything else is wrapped as <c>{ "Value": ... }</c> rather than written bare or dropped.
/// </para>
/// <para>
/// An event too large for <see cref="MaxEventBytes"/> — which a real stack trace does not come near —
/// loses its context first, then payload lines, and stack frames only as a last resort.
/// </para>
/// </remarks>
internal static class CloudWatchEventFormatter
{
    /// <summary>One event's message is kept under this; a longer payload is cut.</summary>
    public const int MaxEventBytes = 256 * 1024;

    private const string Truncated = "...[truncated]";

    /// <summary>Formats <paramref name="logEvent"/> as its CloudWatch JSON, under <see cref="MaxEventBytes"/>.</summary>
    public static string Format(CloudWatchEvent logEvent)
    {
        JsonElement? context = null;
        JsonElement? json = null;
        string[]? lines = null;

        if (AsContext(logEvent.ErrorContext, out var alongside))
            context = alongside;

        if (logEvent.Exception is null)
        {
            if (logEvent.PayloadType == PayloadType.Json && TryParse(logEvent.Payload, out var payload))
            {
                json = payload;
            }
            else if (!string.IsNullOrWhiteSpace(logEvent.Payload))
            {
                // Text, or JSON that does not parse — which stays text rather than being lost.
                lines = Lines(logEvent.Payload);
            }
        }

        var frames = int.MaxValue;

        var message = Serialize(logEvent, context, json, lines, frames);
        if (Encoding.UTF8.GetByteCount(message) <= MaxEventBytes)
            return message;

        // Too large. JSON that was the whole payload survives as its text.
        if (logEvent.Exception is null && (json ?? context) is { } whole)
            lines = Lines(whole.GetRawText());

        message = Serialize(logEvent, context: null, json: null, lines, frames);
        while (Encoding.UTF8.GetByteCount(message) > MaxEventBytes)
        {
            if (lines is { Length: > 1 })
                lines = [.. lines.Take(lines.Length / 2), Truncated];
            else if (lines is { Length: 1 } && lines[0].Length > 64)
                lines = [lines[0][..(lines[0].Length * 3 / 4)] + " " + Truncated];
            else if (logEvent.Exception is not null && frames > 1)
                frames = frames == int.MaxValue ? 200 : frames / 2;
            else
                break;

            message = Serialize(logEvent, context: null, json: null, lines, frames);
        }

        return message;
    }

    // One entry per line, trimmed of the blank lines around them.
    private static string[] Lines(string text) =>
        text.Replace("\r", string.Empty, StringComparison.Ordinal).Trim('\n').Split('\n');

    /// <summary>Any valid JSON value.</summary>
    private static bool TryParse(string? json, out JsonElement element)
    {
        element = default;
        if (string.IsNullOrWhiteSpace(json))
            return false;

        try
        {
            using var document = JsonDocument.Parse(json);
            element = document.RootElement.Clone();
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>The context as a JSON object, always; anything else is wrapped as <c>{ "Value": ... }</c>.</summary>
    private static bool AsContext(string? json, out JsonElement element)
    {
        if (!TryParse(json, out element))
            return false;

        if (element.ValueKind == JsonValueKind.Object)
            return true;

        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WritePropertyName("Value");
            element.WriteTo(writer);
            writer.WriteEndObject();
        }

        using var wrapped = JsonDocument.Parse(stream.ToArray());
        element = wrapped.RootElement.Clone();
        return true;
    }

    private static string Serialize(CloudWatchEvent logEvent, JsonElement? context, JsonElement? json, string[]? lines, int frames)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("Level", logEvent.Level.ToString());
            writer.WriteString("Category", logEvent.Category);
            if (!string.IsNullOrWhiteSpace(logEvent.CorrelationId))
                writer.WriteString("CorrelationId", logEvent.CorrelationId);
            writer.WriteString("Title", logEvent.Title);

            // A delta, present only on the method-tracing events that carry one.
            if (logEvent.Nesting != 0)
                writer.WriteNumber("Nesting", logEvent.Nesting);

            if (context is not null)
            {
                writer.WritePropertyName("Context");
                context.Value.WriteTo(writer);
            }

            if (logEvent.Exception is not null)
            {
                WriteExceptions(writer, logEvent.Exception, frames);
            }
            else if (json is not null)
            {
                writer.WritePropertyName("Payload");
                json.Value.WriteTo(writer);
            }
            else if (lines is { Length: 1 })
            {
                writer.WriteString("Payload", lines[0]);
            }
            else if (lines is not null)
            {
                writer.WriteStartArray("Payload");
                foreach (var line in lines)
                    writer.WriteStringValue(line);
                writer.WriteEndArray();
            }

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    /// <summary>
    /// The exception and every inner exception, outermost first, each with its whole stack trace, one frame
    /// per array entry so the console shows it line by line. <paramref name="frames"/> is lowered only when
    /// the event would not otherwise fit.
    /// </summary>
    private static void WriteExceptions(Utf8JsonWriter writer, Exception outermost, int frames)
    {
        writer.WriteStartArray("Exceptions");

        // No limit on the chain; only a chain that loops back on itself is stopped.
        var seen = new HashSet<Exception>(ReferenceEqualityComparer.Instance);
        for (var exception = outermost; exception is not null && seen.Add(exception); exception = exception.InnerException)
        {
            writer.WriteStartObject();
            writer.WriteString("Type", exception.GetType().FullName);
            writer.WriteString("Message", exception.Message);

            var stack = string.IsNullOrWhiteSpace(exception.StackTrace)
                ? []
                : Lines(exception.StackTrace).Select(l => l.Trim()).Where(l => l.Length > 0).ToArray();

            writer.WriteStartArray("StackTrace");
            foreach (var frame in stack.Take(frames))
                writer.WriteStringValue(frame);
            if (stack.Length > frames)
                writer.WriteStringValue($"...[{stack.Length - frames} more frame(s) cut to fit CloudWatch's event size]");
            writer.WriteEndArray();

            writer.WriteEndObject();
        }

        writer.WriteEndArray();
    }
}
