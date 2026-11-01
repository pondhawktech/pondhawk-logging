// Copyright (c) Pond Hawk Technologies Inc. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System.Text;
using Pondhawk.Logging.Lines;

namespace Pondhawk.Logging.Journald;

/// <summary>
/// The event as the one line <c>AddJournaldConsole</c> writes: the sd-daemon priority prefix (<c>&lt;N&gt;</c>),
/// the category, the title, and the exception chain inline with its stack traces on the same line. Written to
/// stdout when there is no journal, or when a send to it fails — under systemd stdout is the journal too, so
/// the event is kept as text.
/// </summary>
internal static class ConsoleLine
{
    public static string Format(CapturedEvent logEvent)
    {
        var line = new StringBuilder()
            .Append('<').Append(JournaldLine.Priority(logEvent.Level)).Append('>')
            .Append(logEvent.Category).Append(": ")
            .Append(logEvent.Title);

        if (logEvent.Exception is { } exception)
            line.Append(" | ").Append(JournaldLine.Exception(exception));

        return line.ToString();
    }
}
