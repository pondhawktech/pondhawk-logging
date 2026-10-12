// Copyright (c) Pond Hawk Technologies Inc. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.AspNetCore.Routing;
using Microsoft.Net.Http.Headers;

namespace Pondhawk.Logging.AspNetCore;

/// <summary>
/// Writes a request, and then its response, out as one JSON object each for the request dump — with the values
/// the application named replaced by their length. JSON rather than text because a viewer can do more with it:
/// Watch indents it, colours it and folds it, so the headers collapse and a JSON body reads as JSON.
/// </summary>
internal static class HttpDescriber
{
    /// <summary>What stands in for a masked value that has no length worth showing — a JSON number, object or array.</summary>
    public const string Masked = "***";

    private const string FormContentType = "application/x-www-form-urlencoded";

    // Relaxed: the payload is read by people, and the default encoder turns every & and + in a URL into \uXXXX.
    private static readonly JsonWriterOptions WriterOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>Describes <paramref name="request"/>, reading (and rewinding) its body when it is to be written.</summary>
    public static async ValueTask<string> DescribeRequestAsync(HttpRequest request, DiagnosticsOptions options, CancellationToken cancellationToken)
    {
        var buffer = new ArrayBufferWriter<byte>();

        using (var writer = new Utf8JsonWriter(buffer, WriterOptions))
        {
            writer.WriteStartObject();
            writer.WriteString("Method", request.Method);

            // The URL exactly as sent, so an encoding problem shows; the query again below, decoded, for reading.
            var query = request.QueryString.Value;
            var hasQuery = !string.IsNullOrEmpty(query) && query.Length > 1;
            var path = string.Concat(request.PathBase.Value, request.Path.Value);

            writer.WriteString("Url", hasQuery ? string.Concat(path, "?", MaskPairs(query![1..], options.MaskedQueryParameters)) : path);

            if (hasQuery)
                WriteFields(writer, "Query", Pairs(query![1..], options.MaskedQueryParameters, form: false));

            WriteEndpoint(writer, request.HttpContext);
            WriteUser(writer, request.HttpContext, options);
            WriteHeaders(writer, request.Headers, options.MaskedHeaders);

            if (HasBody(request))
                await WriteRequestBodyAsync(writer, request, options, cancellationToken).ConfigureAwait(false);

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    /// <summary>
    /// Describes the response to a request: status, elapsed time, headers and — when <paramref name="capture"/>
    /// was put in place to keep it — the body.
    /// </summary>
    public static string DescribeResponse(HttpResponse response, DiagnosticsOptions options, ResponseCaptureStream? capture, double elapsed)
    {
        var buffer = new ArrayBufferWriter<byte>();

        using (var writer = new Utf8JsonWriter(buffer, WriterOptions))
        {
            writer.WriteStartObject();
            writer.WriteNumber("Status", response.StatusCode);
            writer.WriteNumber("Elapsed", Math.Round(elapsed, 1));

            WriteHeaders(writer, response.Headers, options.MaskedHeaders);

            if (capture is { Total: > 0 })
                WriteResponseBody(writer, capture, options);

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    /// <summary>
    /// Why a body of this content type and encoding is not written, or null when it is: it has to be text, and
    /// not compressed — a compressed body read as text is noise.
    /// </summary>
    public static string? WhyNotWritten(string? contentType, IHeaderDictionary headers)
    {
        if (!IsText(contentType))
            return "not text";

        var encoding = headers.ContentEncoding.ToString();

        return encoding.Length == 0 || string.Equals(encoding, "identity", StringComparison.OrdinalIgnoreCase) ? null : "content-encoded";
    }

    /// <summary>What stands in for a hidden value: its length, so a missing or truncated one still shows.</summary>
    public static string Mask(int length) => string.Create(CultureInfo.InvariantCulture, $"***({length})");

    /// <summary>
    /// Masks the values of the named parameters in a query string or form body (<c>a=1&amp;b=2</c>), leaving
    /// everything else exactly as it was sent.
    /// </summary>
    public static string MaskPairs(string pairs, ISet<string> masked)
    {
        if (masked.Count == 0 || pairs.Length == 0)
            return pairs;

        var builder = new StringBuilder(pairs.Length);

        foreach (var pair in pairs.Split('&'))
        {
            if (builder.Length > 0)
                builder.Append('&');

            var equals = pair.IndexOf('=', StringComparison.Ordinal);

            if (equals >= 0 && masked.Contains(Decode(pair[..equals])))
                builder.Append(pair, 0, equals + 1).Append(Mask(pair.Length - equals - 1));
            else
                builder.Append(pair);
        }

        return builder.ToString();
    }

    /// <summary>
    /// A header's value, or its length when the header is masked. What says something without giving the secret
    /// away is kept: an authorization scheme, cookie names, and a <c>Set-Cookie</c>'s attributes.
    /// </summary>
    public static string Header(string name, string value, ISet<string> masked)
    {
        if (!masked.Contains(name))
            return value;

        if (string.Equals(name, HeaderNames.Cookie, StringComparison.OrdinalIgnoreCase))
            return MaskCookies(value, count: int.MaxValue);

        if (string.Equals(name, HeaderNames.SetCookie, StringComparison.OrdinalIgnoreCase))
            return MaskCookies(value, count: 1);

        var isAuthorization = string.Equals(name, HeaderNames.Authorization, StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, HeaderNames.ProxyAuthorization, StringComparison.OrdinalIgnoreCase);

        var space = isAuthorization ? value.IndexOf(' ', StringComparison.Ordinal) : -1;

        return space > 0
            ? string.Concat(value.AsSpan(0, space + 1), Mask(value.Length - space - 1))
            : Mask(value.Length);
    }

    // "a=1; b=2" with the values of the first count pairs masked. A Cookie header is all pairs; a Set-Cookie is one
    // pair followed by attributes (Path, Expires, SameSite), which are worth reading and give nothing away.
    private static string MaskCookies(string value, int count)
    {
        var builder = new StringBuilder(value.Length);

        foreach (var part in value.Split(';'))
        {
            if (builder.Length > 0)
                builder.Append(';');

            if (count-- <= 0)
            {
                builder.Append(part);
                continue;
            }

            var equals = part.IndexOf('=', StringComparison.Ordinal);

            if (equals < 0)
                builder.Append(Mask(part.Trim().Length));
            else
                builder.Append(part, 0, equals + 1).Append(Mask(part.Length - equals - 1));
        }

        return builder.ToString();
    }

    /// <summary>Whether a body of this content type is text worth writing: JSON, XML, <c>text/*</c> or a form post.</summary>
    public static bool IsText(string? contentType) => TryParse(contentType, out var media) && IsText(media);

    private static bool IsText(MediaTypeHeaderValue media)
    {
        if (media.Type.Equals("text", StringComparison.OrdinalIgnoreCase) || media.MediaType.Equals(FormContentType, StringComparison.OrdinalIgnoreCase))
            return true;

        return IsJsonOrXml(media.SubTypeWithoutSuffix) || IsJsonOrXml(media.Suffix);

        static bool IsJsonOrXml(Microsoft.Extensions.Primitives.StringSegment name) =>
            name.Equals("json", StringComparison.OrdinalIgnoreCase) || name.Equals("xml", StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryParse(string? contentType, out MediaTypeHeaderValue media)
    {
        if (MediaTypeHeaderValue.TryParse(contentType, out var parsed) && parsed is not null)
        {
            media = parsed;
            return true;
        }

        media = null!;
        return false;
    }

    private static bool HasBody(HttpRequest request)
    {
        if (request.ContentLength is { } length)
            return length > 0;

        return request.HttpContext.Features.Get<IHttpRequestBodyDetectionFeature>()?.CanHaveBody
            ?? request.Headers.ContainsKey(HeaderNames.TransferEncoding);
    }

    // Where routing sent the request. This is why the dump sits deep in the pipeline: earlier there is none.
    private static void WriteEndpoint(Utf8JsonWriter writer, HttpContext context)
    {
        if (context.GetEndpoint() is not { } endpoint)
            return;

        writer.WriteStartObject("Endpoint");
        writer.WriteString("Name", endpoint.DisplayName);

        if (endpoint is RouteEndpoint { RoutePattern.RawText: { } route })
            writer.WriteString("Route", route);

        if (context.Request.RouteValues.Count > 0)
        {
            writer.WriteStartObject("Values");

            foreach (var value in context.Request.RouteValues)
                writer.WriteString(value.Key, Convert.ToString(value.Value, CultureInfo.InvariantCulture));

            writer.WriteEndObject();
        }

        writer.WriteEndObject();
    }

    // Who authentication made of the caller; nothing for an anonymous request.
    private static void WriteUser(Utf8JsonWriter writer, HttpContext context, DiagnosticsOptions options)
    {
        if (context.User.Identity is not { IsAuthenticated: true } identity)
            return;

        writer.WriteStartObject("User");

        if (identity.Name is { } name)
            writer.WriteString("Name", name);

        if (options.Subject?.Invoke(context) is { Length: > 0 } subject)
            writer.WriteString("Subject", subject);

        if (identity.AuthenticationType is { } scheme)
            writer.WriteString("Scheme", scheme);

        writer.WriteEndObject();
    }

    private static void WriteHeaders(Utf8JsonWriter writer, IHeaderDictionary headers, ISet<string> masked)
    {
        writer.WriteStartObject("Headers");

        foreach (var header in headers)
        {
            if (header.Value.Count <= 1)
            {
                writer.WriteString(header.Key, Header(header.Key, header.Value.ToString(), masked));
                continue;
            }

            writer.WriteStartArray(header.Key);

            foreach (var value in header.Value)
                writer.WriteStringValue(Header(header.Key, value ?? string.Empty, masked));

            writer.WriteEndArray();
        }

        writer.WriteEndObject();
    }

    // A query string or form body as decoded names and values, in the order sent. A masked query value is its
    // length as sent, the same figure the Url shows. A masked form value is its decoded length (a name sent bare
    // counting as an empty value), because that is all a form already parsed can give, and both must agree.
    private static IEnumerable<KeyValuePair<string, string>> Pairs(string pairs, ISet<string> masked, bool form)
    {
        foreach (var pair in pairs.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var equals = pair.IndexOf('=', StringComparison.Ordinal);
            var name = Decode(equals < 0 ? pair : pair[..equals]);
            var raw = equals < 0 ? string.Empty : pair[(equals + 1)..];

            if (!masked.Contains(name))
                yield return new(name, Decode(raw));
            else if (form)
                yield return new(name, Mask(Decode(raw).Length));
            else
                yield return new(name, equals >= 0 ? Mask(raw.Length) : string.Empty);
        }
    }

    // An object of names and values, in the order given; a name given more than once gets an array.
    private static void WriteFields(Utf8JsonWriter writer, string property, IEnumerable<KeyValuePair<string, string>> fields)
    {
        var names = new List<string>();
        var values = new Dictionary<string, List<string>>(StringComparer.Ordinal);

        foreach (var (name, value) in fields)
        {
            if (!values.TryGetValue(name, out var list))
            {
                names.Add(name);
                values[name] = list = [];
            }

            list.Add(value);
        }

        writer.WriteStartObject(property);

        foreach (var name in names)
        {
            var list = values[name];

            if (list.Count == 1)
            {
                writer.WriteString(name, list[0]);
                continue;
            }

            writer.WriteStartArray(name);

            foreach (var value in list)
                writer.WriteStringValue(value);

            writer.WriteEndArray();
        }

        writer.WriteEndObject();
    }

    // The fields of a form that was parsed before the dump got to it. The cap counts each field's name and value
    // as UTF-8, and only whole fields are written: the first that does not fit ends the body.
    private static void WriteParsedForm(Utf8JsonWriter writer, IFormCollection form, int cap, long? length, ISet<string> masked)
    {
        var fields = new List<KeyValuePair<string, string>>();
        var written = 0;
        var cut = false;

        foreach (var field in form)
        {
            foreach (var sent in field.Value)
            {
                var value = sent ?? string.Empty;
                var size = (long)Encoding.UTF8.GetByteCount(field.Key) + Encoding.UTF8.GetByteCount(value);

                if (size > cap - written)
                {
                    cut = true;
                    break;
                }

                written += (int)size;
                fields.Add(new(field.Key, masked.Contains(field.Key) ? Mask(value.Length) : value));
            }

            if (cut)
                break;
        }

        WriteFields(writer, "Body", fields);

        if (cut)
            WriteCut(writer, written, length);
    }

    // Body holds what was sent: nested JSON for a JSON body, an object of fields for a form post, otherwise the
    // text. What is said about the body rather than read from it goes beside it — BodyOmitted, BodyCut, BodyError
    // — so it can never be mistaken for the body's own content.
    private static async ValueTask WriteRequestBodyAsync(Utf8JsonWriter writer, HttpRequest request, DiagnosticsOptions options, CancellationToken cancellationToken)
    {
        var cap = Cap(options);
        var reason = !options.CaptureBody || cap <= 0 ? "capture is off" : WhyNotWritten(request.ContentType, request.Headers);

        if (reason is not null || !TryParse(request.ContentType, out var media))
        {
            WriteOmitted(writer, request.ContentType, request.ContentLength, reason ?? "not text");
            return;
        }

        // Whoever parsed the form may have left the stream spent, so the fields are taken from the form itself.
        if (IsForm(media) && request.HttpContext.Features.Get<IFormFeature>()?.Form is { } form)
        {
            WriteParsedForm(writer, form, cap, request.ContentLength, options.MaskedBodyFields);
            return;
        }

        string text;
        bool cut;

        try
        {
            (text, cut) = await ReadAsync(request, media.Encoding ?? Encoding.UTF8, cap, cancellationToken).ConfigureAwait(false);
        }
        catch (IOException e)
        {
            // The endpoint will meet the same failure when it reads the body; this only declines to be the one that throws.
            writer.WriteString("BodyError", e.Message);
            return;
        }

        WriteBody(writer, media, text, cut ? cap : null, request.ContentType, request.ContentLength, options.MaskedBodyFields);
    }

    private static void WriteResponseBody(Utf8JsonWriter writer, ResponseCaptureStream capture, DiagnosticsOptions options)
    {
        if (capture.NotWritten is { } reason || !TryParse(capture.ContentType, out var media))
        {
            WriteOmitted(writer, capture.ContentType, capture.Total, capture.NotWritten ?? "not text");
            return;
        }

        var text = (media.Encoding ?? Encoding.UTF8).GetString(capture.Captured);

        WriteBody(writer, media, text, capture.Total > capture.Captured.Length ? capture.Captured.Length : null, capture.ContentType, capture.Total, options.MaskedBodyFields);
    }

    /// <summary>The most of a body that is written, from the options and the limit on them.</summary>
    public static int Cap(DiagnosticsOptions options) => Math.Min(options.MaxBodyBytes, DiagnosticsOptions.LargestMaxBodyBytes);

    // cutAt: how many bytes were kept of a body that ran longer, or null when all of it is here.
    private static void WriteBody(Utf8JsonWriter writer, MediaTypeHeaderValue media, string text, int? cutAt, string? contentType, long? length, ISet<string> masked)
    {
        if (IsForm(media))
        {
            WriteFields(writer, "Body", Pairs(text, masked, form: true));
        }
        else if (!IsJson(media))
        {
            writer.WriteString("Body", text);
        }
        else if (cutAt is not null || !TryWriteJson(writer, "Body", text, masked))
        {
            // JSON that does not parse — malformed, or cut short — can only be written as the text it is, and text
            // cannot be masked by property name. With names to mask, the application's wish wins over the dump.
            if (masked.Count > 0)
            {
                WriteOmitted(writer, contentType, length, "not valid JSON, so the masked fields could not be found");
                return;
            }

            writer.WriteString("Body", text);
        }

        if (cutAt is { } written)
            WriteCut(writer, written, length);
    }

    private static void WriteCut(Utf8JsonWriter writer, int written, long? length)
    {
        writer.WriteStartObject("BodyCut");
        writer.WriteNumber("Written", written);
        WriteLength(writer, length);
        writer.WriteEndObject();
    }

    private static void WriteOmitted(Utf8JsonWriter writer, string? contentType, long? length, string reason)
    {
        writer.WriteStartObject("BodyOmitted");
        writer.WriteString("ContentType", contentType);
        WriteLength(writer, length);
        writer.WriteString("Reason", reason);
        writer.WriteEndObject();
    }

    // A body that says it is JSON and is: written as JSON, so a viewer indents and folds it, with the named
    // properties masked wherever they sit.
    private static bool TryWriteJson(Utf8JsonWriter writer, string property, string text, ISet<string> masked)
    {
        JsonDocument document;

        try
        {
            document = JsonDocument.Parse(text);
        }
        catch (JsonException)
        {
            return false;
        }

        using (document)
        {
            writer.WritePropertyName(property);

            if (masked.Count == 0)
                document.RootElement.WriteTo(writer);
            else
                WriteMasked(writer, document.RootElement, masked);
        }

        return true;
    }

    // Depth is bounded by JsonDocument's own limit (64), so this cannot run away.
    private static void WriteMasked(Utf8JsonWriter writer, JsonElement element, ISet<string> masked)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();

                foreach (var property in element.EnumerateObject())
                {
                    writer.WritePropertyName(property.Name);

                    if (!masked.Contains(property.Name))
                        WriteMasked(writer, property.Value, masked);
                    else if (property.Value.ValueKind == JsonValueKind.String)
                        writer.WriteStringValue(Mask(property.Value.GetString()!.Length));
                    else
                        writer.WriteStringValue(Masked);
                }

                writer.WriteEndObject();
                break;

            case JsonValueKind.Array:
                writer.WriteStartArray();

                foreach (var item in element.EnumerateArray())
                    WriteMasked(writer, item, masked);

                writer.WriteEndArray();
                break;

            default:
                element.WriteTo(writer);
                break;
        }
    }

    private static bool IsForm(MediaTypeHeaderValue media) => media.MediaType.Equals(FormContentType, StringComparison.OrdinalIgnoreCase);

    private static bool IsJson(MediaTypeHeaderValue media) =>
        media.SubTypeWithoutSuffix.Equals("json", StringComparison.OrdinalIgnoreCase) || media.Suffix.Equals("json", StringComparison.OrdinalIgnoreCase);

    private static void WriteLength(Utf8JsonWriter writer, long? length)
    {
        if (length is { } known)
            writer.WriteNumber("Length", known);
        else
            writer.WriteNull("Length");
    }

    // Reads at most cap bytes — one more to learn whether there was more — and rewinds, so the endpoint reads the
    // body from the start. Buffering keeps what was read; the rest is still read by whoever wants it.
    private static async ValueTask<(string Text, bool Cut)> ReadAsync(HttpRequest request, Encoding encoding, int cap, CancellationToken cancellationToken)
    {
        request.EnableBuffering();

        var buffer = ArrayPool<byte>.Shared.Rent(cap + 1);

        try
        {
            var read = 0;

            while (read <= cap)
            {
                var count = await request.Body.ReadAsync(buffer.AsMemory(read, cap + 1 - read), cancellationToken).ConfigureAwait(false);
                if (count == 0)
                    break;

                read += count;
            }

            return (encoding.GetString(buffer, 0, Math.Min(read, cap)), read > cap);
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(buffer);
            request.Body.Position = 0;
        }
    }

    private static string Decode(string name) => Uri.UnescapeDataString(name.Replace('+', ' '));
}
