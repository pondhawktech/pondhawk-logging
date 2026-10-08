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
/// Every event fits <see cref="MaxEventBytes"/>, whatever it carries. The fields nothing else bounds — the
/// title, the category, the correlation id, each exception message, the length of the exception chain — are
/// cut to fixed lengths. An event still too large loses its context first, then payload lines, then stack
/// frames; one that cannot be cut to fit that way is written as its level, category and title alone, marked
/// <c>Truncated</c>.
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

    /// <summary>The title is cut to this many characters.</summary>
    public const int MaxTitleChars = 16_384;

    /// <summary>The category is cut to this many characters.</summary>
    public const int MaxCategoryChars = 1_024;

    /// <summary>The correlation id is cut to this many characters.</summary>
    public const int MaxCorrelationIdChars = 256;

    /// <summary>The subject and the tenant are each cut to this many characters.</summary>
    public const int MaxSubjectChars = 256;

    /// <summary>Each exception's message is cut to this many characters.</summary>
    public const int MaxExceptionMessageChars = 16_384;

    /// <summary>At most this many exceptions of a chain are written.</summary>
    public const int MaxExceptions = 32;

    // What the last-resort event keeps of a title or an exception message.
    private const int MinimalChars = 2_048;

    // Far more passes than cutting ever takes: a line shrinks by a quarter each pass. A bound all the same,
    // so that no input can keep the flush thread here.
    private const int MaxCutPasses = 128;

    private const string Truncated = "...[truncated]";

    /// <summary>Formats <paramref name="logEvent"/> as its CloudWatch JSON, under <see cref="MaxEventBytes"/>.</summary>
    public static string Format(CloudWatchEvent logEvent)
    {
        try
        {
            var message = FormatWhole(logEvent);
            if (Encoding.UTF8.GetByteCount(message) <= MaxEventBytes)
                return message;
        }
        catch
        {
            // Whatever the event carried that could not be written, the event itself is still reported.
        }

        return Minimal(logEvent);
    }

    private static string FormatWhole(CloudWatchEvent logEvent)
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

        for (var pass = 0; pass < MaxCutPasses && Encoding.UTF8.GetByteCount(message) > MaxEventBytes; pass++)
        {
            if (lines is { Length: > 2 })
                lines = [.. lines.Take(lines.Length / 2), Truncated];
            else if (lines is { Length: 2 })
                lines = [lines[0] + " " + Truncated];   // down to one line, which the next branch can shorten
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

    // Who the work was for and which tenant: present only when the unit of work set them.
    private static void WriteWho(Utf8JsonWriter writer, CloudWatchEvent logEvent)
    {
        if (!string.IsNullOrWhiteSpace(logEvent.Subject))
            writer.WriteString("Subject", Cut(logEvent.Subject, MaxSubjectChars));
        if (!string.IsNullOrWhiteSpace(logEvent.Tenant))
            writer.WriteString("Tenant", Cut(logEvent.Tenant, MaxSubjectChars));
    }

    /// <summary>
    /// The event as its level, category and title, and the outermost exception's type and message, each cut
    /// short: what is written when the whole event cannot be made to fit, or could not be written at all.
    /// </summary>
    private static string Minimal(CloudWatchEvent logEvent)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("Level", logEvent.Level.ToString());
            writer.WriteString("Category", Cut(logEvent.Category, MaxCategoryChars));
            if (!string.IsNullOrWhiteSpace(logEvent.CorrelationId))
                writer.WriteString("CorrelationId", Cut(logEvent.CorrelationId, MaxCorrelationIdChars));
            WriteWho(writer, logEvent);
            writer.WriteString("Title", Cut(logEvent.Title, MinimalChars));
            writer.WriteBoolean("Truncated", value: true);

            if (logEvent.Exception is { } exception)
            {
                writer.WriteStartArray("Exceptions");
                writer.WriteStartObject();
                writer.WriteString("Type", exception.GetType().FullName);
                writer.WriteString("Message", Cut(MessageOf(exception), MinimalChars));
                writer.WriteStartArray("StackTrace");
                writer.WriteEndArray();
                writer.WriteEndObject();
                writer.WriteEndArray();
            }

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(stream.ToArray());
    }

    private static string Cut(string? text, int maxChars)
    {
        if (string.IsNullOrEmpty(text))
            return string.Empty;

        return text.Length <= maxChars ? text : text[..maxChars] + " " + Truncated;
    }

    // An exception type can override Message and StackTrace, and an override can throw.
    private static string MessageOf(Exception exception)
    {
        try
        {
            return exception.Message;
        }
        catch (Exception cause)
        {
            return $"[{exception.GetType().Name}.Message threw {cause.GetType().Name}]";
        }
    }

    private static string? StackTraceOf(Exception exception)
    {
        try
        {
            return exception.StackTrace;
        }
        catch
        {
            return null;
        }
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

        try
        {
            // One level deeper than the value just parsed, which may itself have been at the default limit.
            using var wrapped = JsonDocument.Parse(stream.ToArray(), new JsonDocumentOptions { MaxDepth = 128 });
            element = wrapped.RootElement.Clone();
            return true;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    private static string Serialize(CloudWatchEvent logEvent, JsonElement? context, JsonElement? json, string[]? lines, int frames)
    {
        using var stream = new MemoryStream();
        using (var writer = new Utf8JsonWriter(stream))
        {
            writer.WriteStartObject();
            writer.WriteString("Level", logEvent.Level.ToString());
            writer.WriteString("Category", Cut(logEvent.Category, MaxCategoryChars));
            if (!string.IsNullOrWhiteSpace(logEvent.CorrelationId))
                writer.WriteString("CorrelationId", Cut(logEvent.CorrelationId, MaxCorrelationIdChars));
            WriteWho(writer, logEvent);
            writer.WriteString("Title", Cut(logEvent.Title, MaxTitleChars));

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
    /// The exception and its inner exceptions, outermost first, each with its whole stack trace, one frame
    /// per array entry so the console shows it line by line. <paramref name="frames"/> is lowered only when
    /// the event would not otherwise fit.
    /// </summary>
    private static void WriteExceptions(Utf8JsonWriter writer, Exception outermost, int frames)
    {
        writer.WriteStartArray("Exceptions");

        // A chain that loops back on itself is stopped, and so is one longer than anything real.
        var seen = new HashSet<Exception>(ReferenceEqualityComparer.Instance);
        var written = 0;
        for (var exception = outermost; exception is not null && seen.Add(exception); exception = exception.InnerException)
        {
            if (written++ == MaxExceptions)
            {
                writer.WriteStartObject();
                writer.WriteString("Type", Truncated);
                writer.WriteString("Message", $"further inner exceptions cut after the first {MaxExceptions}");
                writer.WriteStartArray("StackTrace");
                writer.WriteEndArray();
                writer.WriteEndObject();
                break;
            }

            writer.WriteStartObject();
            writer.WriteString("Type", exception.GetType().FullName);
            writer.WriteString("Message", Cut(MessageOf(exception), MaxExceptionMessageChars));

            var stackTrace = StackTraceOf(exception);
            var stack = string.IsNullOrWhiteSpace(stackTrace)
                ? []
                : Lines(stackTrace).Select(l => l.Trim()).Where(l => l.Length > 0).ToArray();

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
