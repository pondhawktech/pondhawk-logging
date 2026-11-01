// Copyright (c) Pond Hawk Technologies Inc. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System.Text.Json;

namespace Pondhawk.Logging.Events;

/// <summary>
/// Shapes one event into its <see cref="EventParts"/> and has a writer lay them out, cutting until the
/// written event fits a provider's size limit. Shared by source between the providers, so CloudWatch and the
/// journal show the same content for the same event.
/// </summary>
/// <remarks>
/// <para>
/// The shape is decided by what the event says it carries, never by guessing at the text:
/// </para>
/// <list type="bullet">
/// <item>an exception gives <c>Exceptions</c> — the whole chain, outermost first, each with its
/// <c>Type</c>, <c>Message</c> and a <c>StackTrace</c> of one frame per entry — plus <c>Context</c>
/// from the object <c>ErrorWithContext</c> passed with it;</item>
/// <item>a payload typed as JSON is kept as nested JSON, and stays text only if it does not parse;</item>
/// <item>any other payload is its lines.</item>
/// </list>
/// <para>
/// <c>Context</c> is always a JSON object, so <c>Context.Name</c> can always be queried: a context that
/// serializes to anything else is wrapped as <c>{ "Value": ... }</c> rather than written bare or dropped.
/// </para>
/// <para>
/// The fields nothing else bounds — the title, the category, the correlation id, each exception message, the
/// length of the exception chain — are cut to fixed lengths. An event still too large loses its context
/// first, then payload lines, then stack frames; one that cannot be cut to fit that way is shaped as its
/// level, category and title alone, marked <see cref="EventParts.Minimal"/>. Any of these marks the parts
/// <see cref="EventParts.Cut"/>.
/// </para>
/// <para>
/// <see cref="Shape"/> never throws and always returns, as long as the writer and the size function do not
/// throw on a truncated event: it runs on a provider's delivery path, where an event that threw would be lost
/// and one that looped would stop the provider for the process.
/// </para>
/// </remarks>
internal static class EventShaper
{
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
    // so that no input can keep the delivery path here.
    private const int MaxCutPasses = 128;

    private const string Truncated = "...[truncated]";

    /// <summary>
    /// Shapes <paramref name="logEvent"/> and writes it with <paramref name="write"/>, cut until
    /// <paramref name="size"/> of the written event is at most <paramref name="maxBytes"/>.
    /// </summary>
    public static T Shape<T>(CapturedEvent logEvent, int maxBytes, Func<EventParts, T> write, Func<T, int> size)
    {
        try
        {
            var written = ShapeWhole(logEvent, maxBytes, write, size);
            if (size(written) <= maxBytes)
                return written;
        }
        catch
        {
            // Whatever the event carried that could not be written, the event itself is still reported.
        }

        return write(Minimal(logEvent));
    }

    private static T ShapeWhole<T>(CapturedEvent logEvent, int maxBytes, Func<EventParts, T> write, Func<T, int> size)
    {
        JsonElement? context = null;
        JsonElement? json = null;
        string[]? lines = null;

        if (AsContext(logEvent.ErrorContext, out var alongside))
            context = alongside;

        if (logEvent.Exception is null)
        {
            if (logEvent.PayloadType == LogStateContract.JsonPayload && TryParse(logEvent.Payload, out var payload))
            {
                json = payload;
            }
            else if (!string.IsNullOrWhiteSpace(logEvent.Payload))
            {
                // Text, or JSON that does not parse — which stays text rather than being lost.
                lines = Lines(logEvent.Payload);
            }
        }

        var (chain, chainCut) = logEvent.Exception is null ? (null, false) : Chain(logEvent.Exception);
        var capped = chainCut || Capped(logEvent);
        var frames = int.MaxValue;

        var written = write(Parts(logEvent, context, json, lines, chain, frames, capped));
        if (size(written) <= maxBytes)
            return written;

        // Too large. JSON that was the whole payload survives as its text.
        if (logEvent.Exception is null && (json ?? context) is { } whole)
            lines = Lines(whole.GetRawText());

        written = write(Parts(logEvent, context: null, json: null, lines, chain, frames, cut: true));

        for (var pass = 0; pass < MaxCutPasses && size(written) > maxBytes; pass++)
        {
            if (lines is { Length: > 2 })
                lines = [.. lines.Take(lines.Length / 2), Truncated];
            else if (lines is { Length: 2 })
                lines = [lines[0] + " " + Truncated];   // down to one line, which the next branch can shorten
            else if (lines is { Length: 1 } && lines[0].Length > 64)
                lines = [lines[0][..(lines[0].Length * 3 / 4)] + " " + Truncated];
            else if (chain is not null && frames > 1)
                frames = frames == int.MaxValue ? 200 : frames / 2;
            else
                break;

            written = write(Parts(logEvent, context: null, json: null, lines, chain, frames, cut: true));
        }

        return written;
    }

    private static EventParts Parts(
        CapturedEvent logEvent, JsonElement? context, JsonElement? json, string[]? lines, List<ExceptionPart>? chain, int frames, bool cut) =>
        new()
        {
            Level = logEvent.Level,
            Category = Cut(logEvent.Category, MaxCategoryChars),
            Title = Cut(logEvent.Title, MaxTitleChars),
            CorrelationId = Optional(logEvent.CorrelationId, MaxCorrelationIdChars),
            Subject = Optional(logEvent.Subject, MaxSubjectChars),
            Tenant = Optional(logEvent.Tenant, MaxSubjectChars),
            Nesting = logEvent.Nesting,
            Context = context,

            // An exception takes the place of the payload.
            PayloadJson = chain is null ? json : null,
            PayloadLines = chain is null && json is null ? lines : null,
            Exceptions = chain?.Select(e => WithFrames(e, frames)).ToList(),
            Cut = cut,
        };

    /// <summary>
    /// The event as its level, category and title, and the outermost exception's type and message, each cut
    /// short: what is written when the whole event cannot be made to fit, or could not be written at all.
    /// </summary>
    private static EventParts Minimal(CapturedEvent logEvent) =>
        new()
        {
            Level = logEvent.Level,
            Category = Cut(logEvent.Category, MaxCategoryChars),
            Title = Cut(logEvent.Title, MinimalChars),
            CorrelationId = Optional(logEvent.CorrelationId, MaxCorrelationIdChars),
            Subject = Optional(logEvent.Subject, MaxSubjectChars),
            Tenant = Optional(logEvent.Tenant, MaxSubjectChars),
            Cut = true,
            Minimal = true,
            Exceptions = logEvent.Exception is { } exception
                ? [new ExceptionPart(exception.GetType().FullName, Cut(MessageOf(exception), MinimalChars), [])]
                : null,
        };

    /// <summary>
    /// The exception and its inner exceptions, outermost first, each with its whole stack trace, one frame
    /// per entry; and whether any of it was cut, a message or the chain itself.
    /// </summary>
    private static (List<ExceptionPart> Chain, bool Cut) Chain(Exception outermost)
    {
        var chain = new List<ExceptionPart>();
        var cut = false;

        // A chain that loops back on itself is stopped, and so is one longer than anything real.
        var seen = new HashSet<Exception>(ReferenceEqualityComparer.Instance);
        for (var exception = outermost; exception is not null && seen.Add(exception); exception = exception.InnerException)
        {
            if (chain.Count == MaxExceptions)
            {
                chain.Add(new ExceptionPart(Truncated, $"further inner exceptions cut after the first {MaxExceptions}", []));
                cut = true;
                break;
            }

            var stackTrace = StackTraceOf(exception);
            var stack = string.IsNullOrWhiteSpace(stackTrace)
                ? []
                : Lines(stackTrace).Select(l => l.Trim()).Where(l => l.Length > 0).ToArray();

            var message = MessageOf(exception);
            cut |= message.Length > MaxExceptionMessageChars;
            chain.Add(new ExceptionPart(exception.GetType().FullName, Cut(message, MaxExceptionMessageChars), stack));
        }

        return (chain, cut);
    }

    // Whether any of the fields with a fixed cap is over it.
    private static bool Capped(CapturedEvent logEvent) =>
        logEvent.Title.Length > MaxTitleChars
        || logEvent.Category.Length > MaxCategoryChars
        || Optional(logEvent.CorrelationId, int.MaxValue)?.Length > MaxCorrelationIdChars
        || Optional(logEvent.Subject, int.MaxValue)?.Length > MaxSubjectChars
        || Optional(logEvent.Tenant, int.MaxValue)?.Length > MaxSubjectChars;

    // The stack trace cut to its first frames, lowered only when the event would not otherwise fit.
    private static ExceptionPart WithFrames(ExceptionPart exception, int frames) =>
        exception.StackTrace.Count <= frames
            ? exception
            : exception with
            {
                StackTrace = [.. exception.StackTrace.Take(frames), $"...[{exception.StackTrace.Count - frames} more frame(s) cut to fit the event size]"],
            };

    private static string Cut(string? text, int maxChars)
    {
        if (string.IsNullOrEmpty(text))
            return string.Empty;

        return text.Length <= maxChars ? text : text[..maxChars] + " " + Truncated;
    }

    // Present only when set.
    private static string? Optional(string? text, int maxChars) =>
        string.IsNullOrWhiteSpace(text) ? null : Cut(text, maxChars);

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
}
