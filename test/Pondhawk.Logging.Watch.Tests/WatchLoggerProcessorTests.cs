// Copyright (c) Pond Hawk Technologies Inc. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System.Diagnostics;
using System.Drawing;
using System.Net;
using Microsoft.Extensions.Logging;
using Pondhawk.Logging;
using Pondhawk.Logging.Watch.Tests.Http;
using Shouldly;
using Xunit;
using ZLogger;

namespace Pondhawk.Logging.Watch.Tests;

/// <summary>
/// End-to-end tests: a Microsoft.Extensions.Logging factory whose ZLogger provider delivers through a
/// <see cref="WatchLoggerProcessor"/>, asserting the MemoryPack batches posted to the (mock) Watch server.
/// </summary>
public class WatchLoggerProcessorTests
{
    private static HttpClient CreateClient(MockHttpHandler handler)
        => new(handler) { BaseAddress = new Uri("http://localhost/") };

    private static (ILoggerFactory Factory, List<LogEvent> Delivered) Build(MockHttpHandler handler, SwitchSource switches)
    {
        var delivered = new List<LogEvent>();

        handler.SetHandler(async (req, ct) =>
        {
            var stream = await req.Content.ReadAsStreamAsync(ct);
            var batch = await LogEventBatchSerializer.FromStream(stream);
            if (batch is not null)
            {
                lock (delivered)
                    delivered.AddRange(batch.Events);
            }

            return new HttpResponseMessage(HttpStatusCode.OK);
        });

        var processor = new WatchLoggerProcessor(
            CreateClient(handler), switches, "TestDomain", batchSize: 1, flushInterval: TimeSpan.FromMilliseconds(20));

        var factory = LoggerFactory.Create(b =>
        {
            b.SetMinimumLevel(LogLevel.Trace);
            b.AddZLoggerLogProcessor((_, _) => processor);
        });

        return (factory, delivered);
    }

    private static async Task<LogEvent> WaitForFirst(List<LogEvent> delivered)
    {
        for (var i = 0; i < 100; i++)
        {
            lock (delivered)
            {
                if (delivered.Count > 0)
                    return delivered[0];
            }

            await Task.Delay(20);
        }

        lock (delivered)
            return delivered.FirstOrDefault();
    }

    // ── Rebindable destination ──

    private sealed record Received(Uri Uri, string Domain, LogEventBatch Batch);

    private static void CollectInto(MockHttpHandler handler, List<Received> received)
    {
        handler.SetHandler(async (req, ct) =>
        {
            var uri = req.RequestUri;
            var domain = req.Content.Headers.TryGetValues("X-Domain", out var values)
                ? values.FirstOrDefault()
                : null;

            var stream = await req.Content.ReadAsStreamAsync(ct);
            var batch = await LogEventBatchSerializer.FromStream(stream);

            if (batch is not null)
            {
                lock (received)
                    received.Add(new Received(uri, domain, batch));
            }

            return new HttpResponseMessage(HttpStatusCode.OK);
        });
    }

    private static (ILoggerFactory Factory, List<Received> Received, WatchLoggerProcessor Processor) BuildRebindable(
        MockHttpHandler handler,
        WatchDestination destination,
        int batchSize = 1,
        int flushMs = 20,
        bool collect = true)
    {
        var received = new List<Received>();

        if (collect)
            CollectInto(handler, received);

        // No BaseAddress: the destination supplies absolute URIs, and must keep doing so across a rebind.
        var processor = new WatchLoggerProcessor(
            new HttpClient(handler),
            new SwitchSource(),
            destination,
            batchSize,
            TimeSpan.FromMilliseconds(flushMs));

        var factory = LoggerFactory.Create(b =>
        {
            b.SetMinimumLevel(LogLevel.Trace);
            b.AddZLoggerLogProcessor((_, _) => processor);
        });

        return (factory, received, processor);
    }

    private static async Task<bool> WaitUntil(Func<bool> condition)
    {
        for (var i = 0; i < 150; i++)
        {
            if (condition())
                return true;

            await Task.Delay(20);
        }

        return condition();
    }

    private static int Count(List<Received> received)
    {
        lock (received)
            return received.Sum(r => r.Batch.Events.Count);
    }

    [Fact]
    public async Task Rebind_MovesSubsequentBatches_ToTheNewServerAndDomain()
    {
        var handler = new MockHttpHandler();
        var destination = new WatchDestination("http://first.example", "DomainA");
        var (factory, received, _) = BuildRebindable(handler, destination);

        factory.CreateLogger("X").LogInformation("before");
        (await WaitUntil(() => Count(received) >= 1)).ShouldBeTrue();

        destination.Rebind("http://second.example", "DomainB").ShouldBeTrue();

        factory.CreateLogger("X").LogInformation("after");
        (await WaitUntil(() => Count(received) >= 2)).ShouldBeTrue();

        Received first, last;
        lock (received)
        {
            first = received[0];
            last = received[^1];
        }

        first.Uri.ShouldBe(new Uri("http://first.example/api/sink"));
        first.Domain.ShouldBe("DomainA");
        first.Batch.Domain.ShouldBe("DomainA");

        last.Uri.ShouldBe(new Uri("http://second.example/api/sink"));
        last.Domain.ShouldBe("DomainB");
        last.Batch.Domain.ShouldBe("DomainB");
    }

    [Fact]
    public async Task Rebind_DropsNoEventsAlreadyQueued()
    {
        // A long flush interval leaves the events sitting in the channel when the rebind lands. Nothing is
        // torn down, so they are still delivered — the requirement that made this rebindable rather than a
        // matter of rebuilding the logging factory.
        var handler = new MockHttpHandler();
        var destination = new WatchDestination("http://first.example", "DomainA");
        var (factory, received, _) = BuildRebindable(handler, destination, batchSize: 100, flushMs: 250);

        var logger = factory.CreateLogger("X");
        logger.LogInformation("one");
        logger.LogInformation("two");
        logger.LogInformation("three");

        destination.Rebind("http://second.example", "DomainB");

        (await WaitUntil(() => Count(received) >= 3)).ShouldBeTrue();
        Count(received).ShouldBe(3);
    }

    [Fact]
    public async Task Rebind_ResetsTheCircuitBreaker_SoTheNewServerIsNotHeldShut()
    {
        var handler = new MockHttpHandler();
        handler.ThrowOnSend(new HttpRequestException("the old server is unreachable"));

        var destination = new WatchDestination("http://down.example", "DomainA");
        var (factory, received, processor) = BuildRebindable(handler, destination, collect: false);

        var logger = factory.CreateLogger("X");
        for (var i = 0; i < processor.FailureThreshold; i++)
            logger.LogError("failing {N}", i);

        (await WaitUntil(() => processor.IsCircuitOpen)).ShouldBeTrue();

        // The circuit's backoff is measured in seconds; without a reset the next event would be buffered
        // rather than delivered, whichever server it now belongs to.
        CollectInto(handler, received);
        destination.Rebind("http://up.example", "DomainB").ShouldBeTrue();

        logger.LogError("after the rebind");

        (await WaitUntil(() => Count(received) >= 1)).ShouldBeTrue();
        processor.IsCircuitOpen.ShouldBeFalse();

        lock (received)
            received[^1].Uri.ShouldBe(new Uri("http://up.example/api/sink"));
    }

    [Fact]
    public async Task Delivers_LogEvent_WithCategoryTitleAndLevel()
    {
        var handler = new MockHttpHandler();
        var (factory, delivered) = Build(handler, new SwitchSource());

        factory.CreateLogger("My.Category").LogInformation("hello");

        var e = await WaitForFirst(delivered);
        e.ShouldNotBeNull();
        e.Category.ShouldBe("My.Category");
        e.Title.ShouldBe("hello");
        e.Level.ShouldBe((int)LogLevel.Information);
    }

    [Fact]
    public async Task Applies_SwitchColorAndTag()
    {
        var handler = new MockHttpHandler();
        var switches = new SwitchSource();
        switches.WhenMatched("My", "MyTag", LogLevel.Trace, Color.Red);
        var (factory, delivered) = Build(handler, switches);

        factory.CreateLogger("My.Service").LogWarning("warn");

        var e = await WaitForFirst(delivered);
        e.ShouldNotBeNull();
        e.Color.ShouldBe(Color.Red.ToArgb());
        e.Tag.ShouldBe("MyTag");
    }

    [Fact]
    public async Task Captures_CorrelationId_FromActivityBaggage()
    {
        var handler = new MockHttpHandler();
        var (factory, delivered) = Build(handler, new SwitchSource());

        using var activity = new Activity("test");
        activity.Start();
        activity.SetBaggage(LogPropertyNames.CorrelationBaggageKey, "CID-123");

        factory.CreateLogger("C").LogInformation("x");

        var e = await WaitForFirst(delivered);
        e.ShouldNotBeNull();
        e.CorrelationId.ShouldBe("CID-123");

        activity.Stop();
    }

    [Fact]
    public async Task Delivers_LogObject_AsJsonPayload()
    {
        var handler = new MockHttpHandler();
        var (factory, delivered) = Build(handler, new SwitchSource());

        factory.CreateLogger("P").LogObject("the-widget", new { Name = "W" });

        var e = await WaitForFirst(delivered);
        e.ShouldNotBeNull();
        e.Title.ShouldBe("the-widget");
        e.Type.ShouldBe((int)PayloadType.Json);
        e.Payload.ShouldContain("W");
    }

    [Fact]
    public async Task Delivers_Exception_AsErrorTypeAndTextPayload()
    {
        var handler = new MockHttpHandler();
        var (factory, delivered) = Build(handler, new SwitchSource());

        factory.CreateLogger("E").LogError(new InvalidOperationException("boom"), "failed");

        var e = await WaitForFirst(delivered);
        e.ShouldNotBeNull();
        e.ErrorType.ShouldContain("InvalidOperationException");
        e.Type.ShouldBe((int)PayloadType.Text);
        e.Payload.ShouldContain("boom");
    }

    [Fact]
    public async Task Delivers_ErrorWithContext_AsOnePayloadCarryingContextAndException()
    {
        var handler = new MockHttpHandler();
        var (factory, delivered) = Build(handler, new SwitchSource());

        factory.CreateLogger("C").ErrorWithContext(
            new InvalidOperationException("boom"),
            new { InstanceId = "i-0abc" },
            "Failed to deregister");

        var e = await WaitForFirst(delivered);
        e.ShouldNotBeNull();
        e.Title.ShouldBe("Failed to deregister");
        e.ErrorType.ShouldContain("InvalidOperationException");
        e.Type.ShouldBe((int)PayloadType.Text);

        // One payload slot on the wire, so the context and the exception detail share it.
        e.Payload.ShouldContain("i-0abc");
        e.Payload.ShouldContain("boom");
        e.Payload!.IndexOf("i-0abc", StringComparison.Ordinal)
            .ShouldBeLessThan(e.Payload.IndexOf("boom", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Delivers_PayloadAtCallerChosenLevel()
    {
        var handler = new MockHttpHandler();
        var (factory, delivered) = Build(handler, new SwitchSource());

        factory.CreateLogger("L").LogJson(LogLevel.Error, "Malformed Mission Plan", "{\"bad\":");

        var e = await WaitForFirst(delivered);
        e.ShouldNotBeNull();
        e.Level.ShouldBe((int)LogLevel.Error);
        e.Type.ShouldBe((int)PayloadType.Json);
        e.Payload.ShouldContain("bad");
    }

    [Fact]
    public async Task Delivers_MethodTrace_WithNesting()
    {
        var handler = new MockHttpHandler();
        var (factory, delivered) = Build(handler, new SwitchSource());

        using (factory.CreateLogger("M").EnterMethod())
        {
        }

        for (var i = 0; i < 100; i++)
        {
            lock (delivered)
            {
                if (delivered.Count > 0)
                    break;
            }

            await Task.Delay(20);
        }

        lock (delivered)
            delivered.ShouldContain(x => x.Nesting == 1);
    }

    [Fact]
    public async Task CircuitBreaker_Opens_AndBuffersCriticalEvents_OnRepeatedFailure()
    {
        var handler = new MockHttpHandler();
        handler.RespondWith(HttpStatusCode.InternalServerError);

        var processor = new WatchLoggerProcessor(
            CreateClient(handler), new SwitchSource(), "D", batchSize: 1, flushInterval: TimeSpan.FromMilliseconds(10))
        {
            FailureThreshold = 2,
        };
        var factory = LoggerFactory.Create(b =>
        {
            b.SetMinimumLevel(LogLevel.Trace);
            b.AddZLoggerLogProcessor((_, _) => processor);
        });
        var logger = factory.CreateLogger("X");

        // Warning+ events are the ones the sink buffers as critical during an outage.
        for (var i = 0; i < 5; i++)
            logger.LogWarning("w{Index}", i);

        for (var i = 0; i < 300 && !processor.IsCircuitOpen; i++)
            await Task.Delay(10);

        processor.IsCircuitOpen.ShouldBeTrue();
        processor.CriticalBufferCount.ShouldBeGreaterThan(0);
    }

    [Fact]
    public async Task CircuitBreaker_Recovers_AndFlushesBufferedEvents_WhenServerReturns()
    {
        var handler = new MockHttpHandler();
        var delivered = new List<LogEvent>();
        var failing = new[] { true };

        handler.SetHandler(async (req, ct) =>
        {
            var batch = await LogEventBatchSerializer.FromStream(await req.Content.ReadAsStreamAsync(ct));
            if (failing[0])
                return new HttpResponseMessage(HttpStatusCode.InternalServerError);
            if (batch is not null)
            {
                lock (delivered)
                    delivered.AddRange(batch.Events);
            }

            return new HttpResponseMessage(HttpStatusCode.OK);
        });

        var processor = new WatchLoggerProcessor(
            CreateClient(handler), new SwitchSource(), "D", batchSize: 1, flushInterval: TimeSpan.FromMilliseconds(10))
        {
            FailureThreshold = 2,
            BaseRetryDelay = TimeSpan.FromMilliseconds(50),
            MaxRetryDelay = TimeSpan.FromMilliseconds(100),
        };
        var factory = LoggerFactory.Create(b =>
        {
            b.SetMinimumLevel(LogLevel.Trace);
            b.AddZLoggerLogProcessor((_, _) => processor);
        });
        var logger = factory.CreateLogger("X");

        logger.LogWarning("buffered-1");
        logger.LogWarning("buffered-2");

        for (var i = 0; i < 300 && !processor.IsCircuitOpen; i++)
            await Task.Delay(10);
        processor.IsCircuitOpen.ShouldBeTrue();

        // Server recovers; keep logging so a send is attempted once the retry window elapses, which
        // flushes the buffered critical events into the next successful batch.
        failing[0] = false;
        for (var i = 0; i < 400 && delivered.Count == 0; i++)
        {
            logger.LogWarning("recover-{Index}", i);
            await Task.Delay(15);
        }

        delivered.ShouldNotBeEmpty();
    }
}
