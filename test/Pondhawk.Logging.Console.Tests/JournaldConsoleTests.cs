// Copyright (c) Pond Hawk Technologies Inc. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System.Text;
using Microsoft.Extensions.Logging;
using Shouldly;
using Xunit;
using ZLogger;

namespace Pondhawk.Logging.Console.Tests;

public class JournaldConsoleTests
{
    // Captures the journald-formatted output for one log call by driving the same formatter config that
    // AddJournaldConsole uses, but writing to a MemoryStream instead of the console.
    private static async Task<string> Capture(Action<ILogger> log)
    {
        var stream = new MemoryStream();
        var factory = LoggerFactory.Create(b =>
        {
            b.SetMinimumLevel(LogLevel.Trace);
            b.AddZLoggerStream(stream, o =>
                o.UsePlainTextFormatter(JournaldConsoleLoggingBuilderExtensions.ConfigureJournald));
        });

        log(factory.CreateLogger("My.Category"));

        await Task.Delay(200);
        factory.Dispose();   // flushes the background writer; MemoryStream.ToArray works after close
        return Encoding.UTF8.GetString(stream.ToArray());
    }

    [Fact]
    public async Task Warning_HasPriority4Prefix_Category_AndNoTimestamp()
    {
        var output = await Capture(l => l.LogWarning("disk low"));

        // No timestamp, no color: the line is exactly the priority prefix + category + message.
        output.Trim().ShouldBe("<4>My.Category: disk low");
    }

    [Fact]
    public async Task Error_HasPriority3Prefix()
    {
        var output = await Capture(l => l.LogError("boom"));

        output.Trim().ShouldBe("<3>My.Category: boom");
    }

    [Fact]
    public async Task Critical_HasPriority2Prefix()
    {
        var output = await Capture(l => l.LogCritical("meltdown"));

        output.Trim().ShouldBe("<2>My.Category: meltdown");
    }

    [Fact]
    public async Task Exception_RenderedInline_AsSingleEntry()
    {
        var output = await Capture(l => l.LogError(new InvalidOperationException("bad"), "failed"));

        output.ShouldContain("<3>My.Category: failed | System.InvalidOperationException: bad");
        // A single physical line (one journald entry) — only the trailing newline.
        output.TrimEnd().ShouldNotContain("\n");
    }
}
