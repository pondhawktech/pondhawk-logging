// Copyright (c) Pond Hawk Technologies Inc. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using ZLogger;

namespace Pondhawk.Logging.Journald;

/// <summary>
/// The ZLogger processor that writes each event to the journal, synchronously, on the calling thread: the
/// event is copied out, formatted and sent inside <see cref="Post"/>. Nothing is queued, so a command-line tool
/// that logs and exits has every line in the journal, and disposing has nothing to flush.
/// </summary>
/// <remarks>
/// With no journal (<c>journal</c> null), or when journald does not take an event, the event is written to
/// stdout as the line <c>AddJournaldConsole</c> writes. Nothing throws into the caller, whatever the event holds.
/// </remarks>
internal sealed class JournaldLoggerProcessor : IAsyncLogProcessor
{
    private readonly JournalSocket? _journal;
    private readonly string _syslogIdentifier;
    private readonly Action<string> _stdout;
    private int _disposed;

    /// <param name="journal">The journal's socket, or null to write every event to stdout.</param>
    /// <param name="syslogIdentifier">The <c>SYSLOG_IDENTIFIER</c> on every event.</param>
    /// <param name="stdout">Writes one line to stdout; <see cref="Console.Out"/> unless a test says otherwise.</param>
    public JournaldLoggerProcessor(JournalSocket? journal, string syslogIdentifier, Action<string>? stdout = null)
    {
        _journal = journal;
        _syslogIdentifier = syslogIdentifier;
        _stdout = stdout ?? Console.Out.WriteLine;
    }

    /// <summary>Whether events go to the journal's socket rather than stdout.</summary>
    public bool ToJournal => _journal is not null;

    public void Post(IZLoggerEntry log)
    {
        try
        {
            if (Volatile.Read(ref _disposed) != 0)
                return;

            Write(CapturedEvent.From(log));
        }
        catch
        {
            // A single malformed entry must never crash the caller's logging path.
        }
        finally
        {
            log.Return();
        }
    }

    internal void Write(CapturedEvent logEvent)
    {
        if (_journal is not null && _journal.TrySend(JournalEventWriter.Format(logEvent, _syslogIdentifier)))
            return;

        _stdout(ConsoleLine.Format(logEvent));
    }

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) == 0)
            _journal?.Dispose();

        return ValueTask.CompletedTask;
    }
}
