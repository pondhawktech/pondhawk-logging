// Copyright (c) Pond Hawk Technologies Inc. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Microsoft.Net.Http.Headers;

namespace Pondhawk.Logging.AspNetCore;

/// <summary>
/// Writes a request out as one JSON object for the request dump — method, URL, query, headers and body — with the
/// values the application named replaced by their length. JSON rather than text because a viewer can do more
/// with it: Watch indents it, colours it and folds it, so the headers collapse and a JSON body reads as JSON.
/// </summary>
internal static class RequestDescriber
{
    private const string FormContentType = "application/x-www-form-urlencoded";

    // Relaxed: the payload is read by people, and the default encoder turns every & and + in a URL into \uXXXX.
    private static readonly JsonWriterOptions WriterOptions = new() { Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    /// <summary>Describes <paramref name="request"/>, reading (and rewinding) its body when it is to be written.</summary>
    public static async ValueTask<string> DescribeAsync(HttpRequest request, DiagnosticsOptions options, CancellationToken cancellationToken)
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
                WritePairs(writer, "Query", query![1..], options.MaskedQueryParameters);

            WriteHeaders(writer, request, options.MaskedHeaders);

            if (HasBody(request))
                await WriteBodyAsync(writer, request, options, cancellationToken).ConfigureAwait(false);

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
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

    /// <summary>A header's value, or its length when the header is masked. An authorization scheme is kept.</summary>
    public static string Header(string name, string value, ISet<string> masked)
    {
        if (!masked.Contains(name))
            return value;

        var isAuthorization = string.Equals(name, HeaderNames.Authorization, StringComparison.OrdinalIgnoreCase)
            || string.Equals(name, HeaderNames.ProxyAuthorization, StringComparison.OrdinalIgnoreCase);

        var space = isAuthorization ? value.IndexOf(' ', StringComparison.Ordinal) : -1;

        return space > 0
            ? string.Concat(value.AsSpan(0, space + 1), Mask(value.Length - space - 1))
            : Mask(value.Length);
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

    private static void WriteHeaders(Utf8JsonWriter writer, HttpRequest request, ISet<string> masked)
    {
        writer.WriteStartObject("Headers");

        foreach (var header in request.Headers)
        {
            if (header.Value.Count > 1 && !masked.Contains(header.Key))
            {
                writer.WriteStartArray(header.Key);

                foreach (var value in header.Value)
                    writer.WriteStringValue(value);

                writer.WriteEndArray();
            }
            else
            {
                writer.WriteString(header.Key, Header(header.Key, header.Value.ToString(), masked));
            }
        }

        writer.WriteEndObject();
    }

    // A query string or form body as an object of decoded names and values, in the order sent; a name sent more
    // than once gets an array. A masked value is its length as sent, the same figure the raw form shows.
    private static void WritePairs(Utf8JsonWriter writer, string property, string pairs, ISet<string> masked)
    {
        var names = new List<string>();
        var values = new Dictionary<string, List<string>>(StringComparer.Ordinal);

        foreach (var pair in pairs.Split('&', StringSplitOptions.RemoveEmptyEntries))
        {
            var equals = pair.IndexOf('=', StringComparison.Ordinal);
            var name = Decode(equals < 0 ? pair : pair[..equals]);
            var raw = equals < 0 ? string.Empty : pair[(equals + 1)..];

            if (!values.TryGetValue(name, out var list))
            {
                names.Add(name);
                values[name] = list = [];
            }

            list.Add(equals >= 0 && masked.Contains(name) ? Mask(raw.Length) : Decode(raw));
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

    // Body holds what was sent: nested JSON for a JSON body, an object of fields for a form post, otherwise the
    // text. What is said about the body rather than read from it goes beside it — BodyOmitted, BodyCut, BodyError
    // — so it can never be mistaken for the body's own content.
    private static async ValueTask WriteBodyAsync(Utf8JsonWriter writer, HttpRequest request, DiagnosticsOptions options, CancellationToken cancellationToken)
    {
        var cap = Math.Min(options.MaxBodyBytes, DiagnosticsOptions.LargestMaxBodyBytes);

        if (!options.CaptureBody || cap <= 0 || !TryParse(request.ContentType, out var media) || !IsText(media))
        {
            writer.WriteStartObject("BodyOmitted");
            writer.WriteString("ContentType", request.ContentType);
            WriteLength(writer, request);
            writer.WriteEndObject();
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

        if (media.MediaType.Equals(FormContentType, StringComparison.OrdinalIgnoreCase))
            WritePairs(writer, "Body", text, options.MaskedFormFields);
        else if (cut || !IsJson(media) || !TryWriteJson(writer, "Body", text))
            writer.WriteString("Body", text);

        if (cut)
        {
            writer.WriteStartObject("BodyCut");
            writer.WriteNumber("Written", cap);
            WriteLength(writer, request);
            writer.WriteEndObject();
        }
    }

    // A body that says it is JSON and is: written as JSON, so a viewer indents and folds it. One that is not —
    // malformed, or cut short — is written as the text it is.
    private static bool TryWriteJson(Utf8JsonWriter writer, string property, string text)
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
            document.RootElement.WriteTo(writer);
        }

        return true;
    }

    private static bool IsJson(MediaTypeHeaderValue media) =>
        media.SubTypeWithoutSuffix.Equals("json", StringComparison.OrdinalIgnoreCase) || media.Suffix.Equals("json", StringComparison.OrdinalIgnoreCase);

    private static void WriteLength(Utf8JsonWriter writer, HttpRequest request)
    {
        if (request.ContentLength is { } length)
            writer.WriteNumber("Length", length);
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
