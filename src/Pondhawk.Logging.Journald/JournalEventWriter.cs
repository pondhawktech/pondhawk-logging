// Copyright (c) Pond Hawk Technologies Inc. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System.Buffers;
using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using Pondhawk.Logging.Lines;

namespace Pondhawk.Logging.Journald;

/// <summary>
/// Writes one event as a journald native-protocol message: one field per part of the event, so a shipper can
/// route on <c>CATEGORY</c> and turn the fields back into the JSON object the CloudWatch provider writes.
/// </summary>
/// <remarks>
/// <para>
/// The fields are a fixed list, none with journald's leading <c>_</c>:
/// <c>MESSAGE</c> (what <c>journalctl</c> shows: <c>&lt;Category&gt;: &lt;Title&gt;</c>, and the outermost
/// exception's type and message), <c>PRIORITY</c>, <c>SYSLOG_IDENTIFIER</c>, <c>CATEGORY</c>, <c>TITLE</c>,
/// <c>CORRELATION_ID</c>, <c>SUBJECT</c>, <c>TENANT</c>, <c>NESTING</c>, <c>CONTEXT_JSON</c>,
/// <c>PAYLOAD_JSON</c>, <c>PAYLOAD</c>, <c>EXCEPTIONS_JSON</c> and <c>TRUNCATED</c>. A field with nothing to
/// say is left out.
/// </para>
/// <para>
/// <c>PAYLOAD</c> is a text payload's lines joined by <c>\n</c>: one line is CloudWatch's string
/// <c>Payload</c>, several its array. <c>TRUNCATED=1</c> marks an event of which anything was cut
/// (<see cref="EventParts.Cut"/>).
/// </para>
/// <para>
/// A value without a newline is written <c>NAME=value\n</c>; one with a newline as <c>NAME\n</c>, its length
/// as a 64-bit little-endian integer, the value and <c>\n</c>, so any value is carried whole.
/// </para>
/// </remarks>
internal static class JournalEventWriter
{
    /// <summary>An event's message is kept to this many bytes; a larger one is cut.</summary>
    public const int MaxEventBytes = 256 * 1024;

    /// <summary>
    /// Formats <paramref name="logEvent"/> as its native-protocol message, at most <see cref="MaxEventBytes"/>.
    /// </summary>
    public static byte[] Format(CapturedEvent logEvent, string syslogIdentifier) =>
        EventShaper.Shape(logEvent, MaxEventBytes, parts => Write(parts, syslogIdentifier), static message => message.Length);

    /// <summary>What <c>journalctl</c> shows for the event.</summary>
    public static string Message(EventParts parts)
    {
        var message = $"{parts.Category}: {parts.Title}";
        return parts.Exceptions is [var outermost, ..]
            ? $"{message} | {outermost.Type}: {outermost.Message}"
            : message;
    }

    private static byte[] Write(EventParts parts, string syslogIdentifier)
    {
        var buffer = new ArrayBufferWriter<byte>(1_024);

        WriteField(buffer, "MESSAGE", Message(parts));
        WriteField(buffer, "PRIORITY", JournaldLine.Priority(parts.Level).ToString(System.Globalization.CultureInfo.InvariantCulture));
        WriteField(buffer, "SYSLOG_IDENTIFIER", syslogIdentifier);
        WriteField(buffer, "CATEGORY", parts.Category);
        WriteField(buffer, "TITLE", parts.Title);
        WriteField(buffer, "CORRELATION_ID", parts.CorrelationId);
        WriteField(buffer, "SUBJECT", parts.Subject);
        WriteField(buffer, "TENANT", parts.Tenant);

        // A delta, present only on the method-tracing events that carry one.
        if (parts.Nesting != 0)
            WriteField(buffer, "NESTING", parts.Nesting.ToString(System.Globalization.CultureInfo.InvariantCulture));

        if (parts.Context is { } context)
            WriteField(buffer, "CONTEXT_JSON", Json(writer => context.WriteTo(writer)));

        if (parts.Exceptions is { } exceptions)
            WriteField(buffer, "EXCEPTIONS_JSON", Json(writer => WriteExceptions(writer, exceptions)));
        else if (parts.PayloadJson is { } json)
            WriteField(buffer, "PAYLOAD_JSON", Json(writer => json.WriteTo(writer)));
        else if (parts.PayloadLines is { } lines)
            WriteField(buffer, "PAYLOAD", string.Join('\n', lines));

        if (parts.Cut)
            WriteField(buffer, "TRUNCATED", "1");

        return buffer.WrittenSpan.ToArray();
    }

    private static void WriteField(ArrayBufferWriter<byte> buffer, string name, string? value)
    {
        if (value is null)
            return;

        WriteAscii(buffer, name);

        if (value.Contains('\n', StringComparison.Ordinal))
        {
            // Binary-safe form: NAME\n, the length as 64-bit little-endian, the value, \n.
            buffer.GetSpan(1)[0] = (byte)'\n';
            buffer.Advance(1);

            var length = Encoding.UTF8.GetByteCount(value);
            BinaryPrimitives.WriteUInt64LittleEndian(buffer.GetSpan(sizeof(ulong)), (ulong)length);
            buffer.Advance(sizeof(ulong));
        }
        else
        {
            buffer.GetSpan(1)[0] = (byte)'=';
            buffer.Advance(1);
        }

        buffer.Advance(Encoding.UTF8.GetBytes(value, buffer.GetSpan(Encoding.UTF8.GetMaxByteCount(value.Length))));

        buffer.GetSpan(1)[0] = (byte)'\n';
        buffer.Advance(1);
    }

    private static void WriteAscii(ArrayBufferWriter<byte> buffer, string name) =>
        buffer.Advance(Encoding.ASCII.GetBytes(name, buffer.GetSpan(name.Length)));

    private static string Json(Action<Utf8JsonWriter> write)
    {
        var buffer = new ArrayBufferWriter<byte>(256);
        using (var writer = new Utf8JsonWriter(buffer))
            write(writer);

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    // As the CloudWatch provider writes them: one frame per array entry.
    private static void WriteExceptions(Utf8JsonWriter writer, IReadOnlyList<ExceptionPart> exceptions)
    {
        writer.WriteStartArray();
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
