// Copyright (c) Pond Hawk Technologies Inc. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System.Diagnostics;
using Pondhawk.Logging.Journald.Tests.Support;
using Shouldly;
using Xunit;

namespace Pondhawk.Logging.Journald.Tests;

public class JournalDetectionTests
{
    private static readonly (ulong Device, ulong Inode) Stdout = (64, 1001);
    private static readonly (ulong Device, ulong Inode) Stderr = (64, 1002);

    private static (ulong Device, ulong Inode)? Identify(int fd) => fd switch
    {
        1 => Stdout,
        2 => Stderr,
        _ => null,
    };

    [Fact]
    public void AStreamNamingStdout_IsTheJournal() =>
        JournalDetection.UnderJournal("64:1001", socketExists: true, Identify).ShouldBeTrue();

    [Fact]
    public void AStreamNamingStderr_IsTheJournal() =>
        JournalDetection.UnderJournal("64:1002", socketExists: true, Identify).ShouldBeTrue();

    [Fact]
    public void AStreamNamingNeither_IsTheConsole() =>
        JournalDetection.UnderJournal("64:9999", socketExists: true, Identify).ShouldBeFalse();

    [Fact]
    public void ASameInodeOnAnotherDevice_IsTheConsole() =>
        JournalDetection.UnderJournal("65:1001", socketExists: true, Identify).ShouldBeFalse();

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("1001")]
    [InlineData(":1001")]
    [InlineData("64:")]
    [InlineData("64:abc")]
    [InlineData("-64:1001")]
    [InlineData(" 64:1001")]
    public void NoStream_OrOneThatDoesNotParse_IsTheConsole(string journalStream) =>
        JournalDetection.UnderJournal(journalStream, socketExists: true, Identify).ShouldBeFalse();

    [Fact]
    public void NoSocket_IsTheConsole_EvenUnderSystemd() =>
        JournalDetection.UnderJournal("64:1001", socketExists: false, Identify).ShouldBeFalse();

    [Fact]
    public void OutputThatCannotBeIdentified_IsTheConsole() =>
        JournalDetection.UnderJournal("64:1001", socketExists: true, _ => null).ShouldBeFalse();

    [Fact]
    public void Identify_ReadsTheDeviceAndInode_AsStatDoes()
    {
        var path = Path.GetTempFileName();
        try
        {
            using var file = File.OpenRead(path);

            var identity = JournalDetection.Identify((int)file.SafeFileHandle.DangerousGetHandle());

            identity.ShouldBe(StatOf(path));
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public void ThisProcessesStdout_NamedByTheStream_WithTheSocketThere_IsTheJournal()
    {
        using var journal = new FakeJournal();
        var stdout = JournalDetection.Identify(1);
        stdout.ShouldNotBeNull();

        JournalDetection.UnderJournal($"{stdout.Value.Device}:{stdout.Value.Inode}", File.Exists(journal.SocketPath), JournalDetection.Identify)
            .ShouldBeTrue();
    }

    [Fact]
    public void NoSocketAtThePath_GivesNoJournal() =>
        JournalDetection.OpenIfUnderJournal(Path.Combine(Path.GetTempPath(), "jd-none-" + Guid.NewGuid().ToString("N")[..8])).ShouldBeNull();

    private static (ulong Device, ulong Inode) StatOf(string path)
    {
        using var stat = Process.Start(new ProcessStartInfo("stat", ["-c", "%d:%i", path]) { RedirectStandardOutput = true })!;
        var output = stat.StandardOutput.ReadToEnd().Trim();
        stat.WaitForExit();

        JournalDetection.TryParse(output, out var identity).ShouldBeTrue(output);
        return identity;
    }
}
