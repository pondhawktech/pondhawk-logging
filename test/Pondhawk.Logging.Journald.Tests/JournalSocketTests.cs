// Copyright (c) Pond Hawk Technologies Inc. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System.Diagnostics;
using System.Text;
using Pondhawk.Logging.Journald.Tests.Support;
using Shouldly;
using Xunit;

namespace Pondhawk.Logging.Journald.Tests;

public class JournalSocketTests
{
    private static byte[] Message(int payloadBytes) =>
        JournalEventWriter.Format(new TestEvent { PayloadType = 4, Payload = "x\n" + new string('p', payloadBytes) }.Journald(), "my-tool");

    [Fact]
    public void AMessage_GoesAsOneDatagram()
    {
        using var journal = new FakeJournal();
        using var socket = JournalSocket.TryOpen(journal.SocketPath);
        var message = Message(100);

        socket.TrySend(message).ShouldBeTrue();

        var received = journal.Receive();
        received.ByMemfd.ShouldBeFalse();
        received.Message.ShouldBe(message);
    }

    [Fact]
    public void AMessageLargerThanADatagram_GoesByASealedMemfd()
    {
        using var journal = new FakeJournal();
        using var socket = JournalSocket.TryOpen(journal.SocketPath, sendBufferBytes: 16 * 1024);
        var message = Message(100_000);

        socket.TrySend(message).ShouldBeTrue();

        var received = journal.Receive();
        received.ByMemfd.ShouldBeTrue();
        received.Sealed.ShouldBeTrue();
        received.Message.ShouldBe(message);
    }

    [Fact]
    public void AnEventOfTheFullSize_ArrivesWhole()
    {
        using var journal = new FakeJournal();
        using var socket = JournalSocket.TryOpen(journal.SocketPath);
        var message = Message(JournalEventWriter.MaxEventBytes - 1_024);
        message.Length.ShouldBeLessThanOrEqualTo(JournalEventWriter.MaxEventBytes);

        socket.TrySend(message).ShouldBeTrue();

        journal.Receive().Message.ShouldBe(message);
    }

    [Fact]
    public void NoSocketAtThePath_IsAFailedSend()
    {
        using var socket = JournalSocket.TryOpen(Path.Combine(Path.GetTempPath(), "jd-none-" + Guid.NewGuid().ToString("N")[..8]));

        socket.TrySend(Message(10)).ShouldBeFalse();
    }

    [Fact]
    public void AFullSocket_FailsAtOnce_RatherThanWaiting()
    {
        using var journal = new FakeJournal(receiveBufferBytes: 4 * 1024);
        using var socket = JournalSocket.TryOpen(journal.SocketPath);
        var message = Encoding.ASCII.GetBytes("MESSAGE=filler\n");

        var clock = Stopwatch.StartNew();
        var failed = false;
        for (var i = 0; i < 10_000 && !failed; i++)
            failed = !socket.TrySend(message);

        failed.ShouldBeTrue();
        clock.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public void APathTooLongForASocketAddress_GivesNoSocket() =>
        JournalSocket.TryOpen("/" + new string('a', 200)).ShouldBeNull();

    [Fact]
    public void AfterDispose_ASendFails()
    {
        using var journal = new FakeJournal();
        var socket = JournalSocket.TryOpen(journal.SocketPath);
        socket.Dispose();

        socket.TrySend(Message(10)).ShouldBeFalse();
    }
}
