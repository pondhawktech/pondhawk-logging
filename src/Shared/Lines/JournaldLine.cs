// Copyright (c) Pond Hawk Technologies Inc. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System.Text;
using Microsoft.Extensions.Logging;

namespace Pondhawk.Logging.Lines;

/// <summary>
/// The parts of the journald text line that <c>AddJournaldConsole</c> writes and the Journald sink falls back
/// to: the level's syslog priority (the <c>&lt;N&gt;</c> prefix) and an exception chain written inline. Shared by
/// source between the two packages, so the line cannot differ between them.
/// </summary>
internal static class JournaldLine
{
    // A chain longer than anything real, or one that loops back on itself, is stopped here.
    private const int MaxExceptions = 32;

    /// <summary>The syslog priority journald files <paramref name="level"/> under.</summary>
    public static int Priority(LogLevel level) => level switch
    {
        LogLevel.Critical => 2,     // crit
        LogLevel.Error => 3,        // err
        LogLevel.Warning => 4,      // warning
        LogLevel.Information => 6,  // info
        LogLevel.Debug => 7,        // debug
        LogLevel.Trace => 7,        // debug
        _ => 6,
    };

    /// <summary>
    /// The exception and its inner exceptions on one line: <c>Type: Message ---&gt; Inner: Message | stack</c>,
    /// each stack trace's newlines turned into spaces, so the event stays a single journald entry.
    /// </summary>
    public static string Exception(Exception exception)
    {
        var line = new StringBuilder();
        Append(line, exception, depth: 0);
        return line.ToString();
    }

    private static void Append(StringBuilder line, Exception exception, int depth)
    {
        line.Append(exception.GetType().FullName ?? exception.GetType().Name).Append(": ").Append(MessageOf(exception));

        if (exception.InnerException is { } inner && depth < MaxExceptions)
        {
            line.Append(" ---> ");
            Append(line, inner, depth + 1);
        }

        if (StackTraceOf(exception) is { } stackTrace)
            line.Append(" | ").Append(stackTrace.Replace("\r", string.Empty, StringComparison.Ordinal).Replace('\n', ' '));
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
}
