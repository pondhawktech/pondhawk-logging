// Copyright (c) Pond Hawk Technologies Inc. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System.Collections.Concurrent;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Pondhawk.Logging.Journald.Tests.Support;
using Shouldly;
using Xunit;
using ZLogger;

namespace Pondhawk.Logging.Journald.Tests;

public class JournaldLoggerProcessorTests
{
    private static ILoggerFactory Factory(JournaldLoggerProcessor processor) =>
        LoggerFactory.Create(builder =>
        {
            builder.SetMinimumLevel(LogLevel.Trace);
            builder.AddZLoggerLogProcessor(processor);
        });

    [Fact]
    public void AnEvent_ReachesTheJournal_WithItsFields()
    {
        using var journal = new FakeJournal();
        var processor = new JournaldLoggerProcessor(JournalSocket.TryOpen(journal.SocketPath), "my-tool");
        using (var factory = Factory(processor))
            factory.CreateLogger("Orders.Api").LogWarning("Order {OrderId} late", 42);

        var fields = JournalMessage.Map(journal.Receive().Message);
        fields["MESSAGE"].ShouldBe("Orders.Api: Order 42 late");
        fields["PRIORITY"].ShouldBe("4");
        fields["CATEGORY"].ShouldBe("Orders.Api");
        fields["SYSLOG_IDENTIFIER"].ShouldBe("my-tool");
    }

    [Fact]
    public void WhatTheApiPutsOnTheState_ArrivesAsFields()
    {
        using var journal = new FakeJournal();
        var processor = new JournaldLoggerProcessor(JournalSocket.TryOpen(journal.SocketPath), "my-tool");
        using (var factory = Factory(processor))
        {
            var logger = factory.CreateLogger("Orders.Api");
            logger.LogJson(LogLevel.Information, "request", "{\"a\":1}");
            logger.ErrorWithContext(new InvalidOperationException("bad"), new { OrderId = 7 }, "failed");
        }

        var json = JournalMessage.Map(journal.Receive().Message);
        json["TITLE"].ShouldBe("request");
        json["PAYLOAD_JSON"].ShouldBe("{\"a\":1}");

        var error = JournalMessage.Map(journal.Receive().Message);
        error["MESSAGE"].ShouldBe("Orders.Api: failed | System.InvalidOperationException: bad");
        using var context = JsonDocument.Parse(error["CONTEXT_JSON"]);
        context.RootElement.GetProperty("OrderId").GetInt32().ShouldBe(7);
        error.ShouldContainKey("EXCEPTIONS_JSON");
    }

    [Fact]
    public void EveryEvent_IsInTheJournal_WhenThePostReturns()
    {
        using var journal = new FakeJournal();
        var processor = new JournaldLoggerProcessor(JournalSocket.TryOpen(journal.SocketPath), "my-tool");
        var factory = Factory(processor);
        var logger = factory.CreateLogger("Tool");

        for (var i = 0; i < 50; i++)
            logger.LogInformation("line {Line}", i);

        // Nothing was flushed or disposed: each send happened inside its call.
        for (var i = 0; i < 50; i++)
            JournalMessage.Map(journal.Receive().Message)["TITLE"].ShouldBe($"line {i}");

        factory.Dispose();
    }

    [Fact]
    public void AFailedSend_GoesToStdout_AsTheConsoleLine()
    {
        var stdout = new ConcurrentQueue<string>();
        var missing = Path.Combine(Path.GetTempPath(), "jd-none-" + Guid.NewGuid().ToString("N")[..8]);
        var processor = new JournaldLoggerProcessor(JournalSocket.TryOpen(missing), "my-tool", stdout.Enqueue);
        using (var factory = Factory(processor))
            factory.CreateLogger("Orders.Api").LogError("it broke");

        stdout.ShouldBe(["<3>Orders.Api: it broke"]);
    }

    [Fact]
    public void WithNoJournal_EveryEventGoesToStdout()
    {
        var stdout = new ConcurrentQueue<string>();
        var processor = new JournaldLoggerProcessor(journal: null, "my-tool", stdout.Enqueue);
        processor.ToJournal.ShouldBeFalse();

        using (var factory = Factory(processor))
        {
            var logger = factory.CreateLogger("Tool");
            logger.LogInformation("one");
            logger.LogDebug("two");
        }

        stdout.ShouldBe(["<6>Tool: one", "<7>Tool: two"]);
    }

    [Fact]
    public void TheConsoleLine_CarriesTheExceptionChainInline()
    {
        var exception = TestEvent.Thrown(new InvalidOperationException("outer", new ArgumentException("inner")));

        var line = ConsoleLine.Format(new TestEvent { Exception = exception }.Journald());

        line.ShouldStartWith("<3>My.Category: it broke | System.InvalidOperationException: outer ---> System.ArgumentException: inner | ");
        line.ShouldNotContain('\n');
    }

    [Fact]
    public void NothingThrowsIntoTheCaller_WhenStdoutThrows()
    {
        var processor = new JournaldLoggerProcessor(journal: null, "my-tool", _ => throw new IOException("stdout closed"));

        using var factory = Factory(processor);
        Should.NotThrow(() => factory.CreateLogger("Tool").LogError("it broke"));
    }

    [Fact]
    public async Task AfterDispose_EventsAreDropped()
    {
        var stdout = new ConcurrentQueue<string>();
        var processor = new JournaldLoggerProcessor(journal: null, "my-tool", stdout.Enqueue);
        using var factory = Factory(processor);

        await processor.DisposeAsync();
        factory.CreateLogger("Tool").LogError("late");

        stdout.ShouldBeEmpty();
    }
}
