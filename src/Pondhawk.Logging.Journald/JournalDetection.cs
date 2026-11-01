// Copyright (c) Pond Hawk Technologies Inc. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System.Buffers.Binary;
using System.Globalization;
using static Pondhawk.Logging.Journald.NativeMethods;

namespace Pondhawk.Logging.Journald;

/// <summary>
/// Decides, once at start, whether events go to the journal's socket or to the console: the journal only when
/// its socket exists and <c>JOURNAL_STREAM</c> names this process's stdout or stderr — that is, when systemd
/// started the process with its output connected to the journal.
/// </summary>
/// <remarks>
/// A developer's terminal, a test run and a tool run over SSM Run Command all get the console, where their
/// output is read. A child that inherited <c>JOURNAL_STREAM</c> but had its output redirected gets the console
/// too, since its stdout is no longer the stream the variable names.
/// </remarks>
internal static class JournalDetection
{
    /// <summary>The variable systemd sets to the <c>&lt;device&gt;:&lt;inode&gt;</c> of the journal stream.</summary>
    public const string JournalStreamVariable = "JOURNAL_STREAM";

    /// <summary>The journal's socket when events should go to it, otherwise null.</summary>
    public static JournalSocket? OpenIfUnderJournal(string socketPath = JournalSocket.DefaultPath) =>
        UnderJournal(socketPath) ? JournalSocket.TryOpen(socketPath) : null;

    /// <summary>Whether this process's output is the journal and its socket is there to write to.</summary>
    public static bool UnderJournal(string socketPath = JournalSocket.DefaultPath) =>
        UnderJournal(Environment.GetEnvironmentVariable(JournalStreamVariable), File.Exists(socketPath), Identify);

    internal static bool UnderJournal(string? journalStream, bool socketExists, Func<int, (ulong Device, ulong Inode)?> identify)
    {
        if (!socketExists || !TryParse(journalStream, out var stream))
            return false;

        return identify(1) == stream || identify(2) == stream;
    }

    /// <summary>Reads <c>JOURNAL_STREAM</c>: two decimal numbers, <c>&lt;device&gt;:&lt;inode&gt;</c>.</summary>
    internal static bool TryParse(string? value, out (ulong Device, ulong Inode) stream)
    {
        stream = default;
        if (string.IsNullOrWhiteSpace(value))
            return false;

        var colon = value.IndexOf(':', StringComparison.Ordinal);
        if (colon <= 0
            || !ulong.TryParse(value.AsSpan(0, colon), NumberStyles.None, CultureInfo.InvariantCulture, out var device)
            || !ulong.TryParse(value.AsSpan(colon + 1), NumberStyles.None, CultureInfo.InvariantCulture, out var inode))
            return false;

        stream = (device, inode);
        return true;
    }

    /// <summary>The device and inode of descriptor <paramref name="fd"/>, or null when they cannot be read.</summary>
    internal static unsafe (ulong Device, ulong Inode)? Identify(int fd)
    {
        if (!OperatingSystem.IsLinux() || !Environment.Is64BitProcess)
            return null;

        try
        {
            var buffer = stackalloc byte[StatBufferBytes];
            if (Fstat(fd, buffer) < 0)
                return null;

            var stat = new ReadOnlySpan<byte>(buffer, StatBufferBytes);
            return (BinaryPrimitives.ReadUInt64LittleEndian(stat), BinaryPrimitives.ReadUInt64LittleEndian(stat[sizeof(ulong)..]));
        }
        catch
        {
            // No fstat in this libc: undecided, so the console.
            return null;
        }
    }
}
