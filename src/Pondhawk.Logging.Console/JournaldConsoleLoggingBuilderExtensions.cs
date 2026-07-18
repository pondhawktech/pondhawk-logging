// Copyright (c) Pond Hawk Technologies Inc. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System.Buffers;
using System.Text;
using Microsoft.Extensions.Logging;
using ZLogger;
using ZLogger.Formatters;
using ZLogger.Providers;

namespace Pondhawk.Logging.Console;

/// <summary>
/// Adds a ZLogger console optimized for systemd-journald: each line is prefixed with the sd-daemon
/// priority (<c>&lt;N&gt;</c>) mapped from the log level, plus the category, with no timestamp and no ANSI
/// color — journald stamps the time itself and stores raw text, and the <c>&lt;N&gt;</c> prefix sets the
/// entry's <c>PRIORITY</c> so <c>journalctl -p</c> filtering works. Fixed at Warning: intended for a Linux
/// production service console that surfaces only warnings and errors.
/// </summary>
public static class JournaldConsoleLoggingBuilderExtensions
{
    /// <summary>
    /// Adds the journald-optimized ZLogger console, fixed at <see cref="LogLevel.Warning"/>.
    /// </summary>
    /// <param name="builder">The logging builder.</param>
    /// <returns>The logging builder for chaining.</returns>
    public static ILoggingBuilder AddJournaldConsole(this ILoggingBuilder builder)
    {
        // Fixed at Warning: only Warning, Error, and Critical reach this console.
        builder.AddFilter<ZLoggerConsoleLoggerProvider>(category: null, LogLevel.Warning);

        return builder.AddZLoggerConsole(options => options.UsePlainTextFormatter(ConfigureJournald));
    }

    /// <summary>Configures a plain-text formatter to produce journald-optimized lines.</summary>
    internal static void ConfigureJournald(PlainTextZLoggerFormatter formatter)
    {
        // "<priority>category: " prefix. journald parses <N> into PRIORITY; no timestamp, no color.
        formatter.SetPrefixFormatter(
            $"<{0}>{1}: ",
            static (in MessageTemplate template, in LogInfo info) =>
                template.Format(Priority(info.LogLevel), info.Category.Name));

        // Render exceptions inline on the same (priority-prefixed) line so each event is a single journald
        // entry, rather than continuation lines that would lose the priority.
        formatter.SetExceptionFormatter(WriteExceptionInline);
    }

    // Microsoft.Extensions.Logging.LogLevel -> syslog priority (used by the sd-daemon <N> prefix).
    private static int Priority(LogLevel level) => level switch
    {
        LogLevel.Critical => 2,     // crit
        LogLevel.Error => 3,        // err
        LogLevel.Warning => 4,      // warning
        LogLevel.Information => 6,  // info
        LogLevel.Debug => 7,        // debug
        LogLevel.Trace => 7,        // debug
        _ => 6,
    };

    private static void WriteExceptionInline(IBufferWriter<byte> writer, Exception exception)
    {
        WriteUtf8(writer, " | ");
        WriteException(writer, exception);
    }

    private static void WriteException(IBufferWriter<byte> writer, Exception exception)
    {
        WriteUtf8(writer, exception.GetType().FullName ?? exception.GetType().Name);
        WriteUtf8(writer, ": ");
        WriteUtf8(writer, exception.Message);

        if (exception.InnerException is { } inner)
        {
            WriteUtf8(writer, " ---> ");
            WriteException(writer, inner);
        }

        if (exception.StackTrace is { } stackTrace)
        {
            WriteUtf8(writer, " | ");
            WriteUtf8(writer, CollapseNewlines(stackTrace));
        }
    }

    private static string CollapseNewlines(string value) =>
        value.Replace("\r", string.Empty, StringComparison.Ordinal).Replace('\n', ' ');

    private static void WriteUtf8(IBufferWriter<byte> writer, string value)
    {
        var span = writer.GetSpan(Encoding.UTF8.GetMaxByteCount(value.Length));
        var written = Encoding.UTF8.GetBytes(value.AsSpan(), span);
        writer.Advance(written);
    }
}
