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
