// Copyright (c) Pond Hawk Technologies Inc. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System.Diagnostics;
using System.Net;
using Microsoft.Extensions.Logging;
using Pondhawk.Logging.Watch.Tests.Http;
using Shouldly;
using Xunit;
using ZLogger;

namespace Pondhawk.Logging.Watch.Tests;

/// <summary>
/// Retro logging in the processor: a marked unit of work's Debug events below the switch are kept, and sent before
/// the first Error in the same unit of work. The level gate is the filter's job (<see cref="AddWatchTests"/>); here
/// the floor is open, as the filter leaves it for a marked unit of work.
/// </summary>
public class RetroLoggingTests
{
    private static (ILoggerFactory Factory, List<LogEvent> Delivered, WatchLoggerProcessor Processor) Build(RetroOptions retro = null)
    {
        var delivered = new List<LogEvent>();
        var handler = new MockHttpHandler();
        handler.SetHandler(async (req, ct) =>
        {
            var batch = await LogEventBatchSerializer.FromStream(await req.Content.ReadAsStreamAsync(ct));
            if (batch is not null)
            {
                lock (delivered)
                    delivered.AddRange(batch.Events);
            }

            return new HttpResponseMessage(HttpStatusCode.OK);
        });

        var switches = new SwitchSource();
        switches.WhenNotMatched(LogLevel.Warning);

        var processor = new WatchLoggerProcessor(
            new HttpClient(handler) { BaseAddress = new Uri("http://localhost/") },
            switches,
            WatchDestination.Relative("TestDomain"),
            batchSize: 1,
            flushInterval: TimeSpan.FromMilliseconds(20),
            ownsDependencies: false,
            defaultTag: null,
            retro);

        var factory = LoggerFactory.Create(b =>
        {
            b.SetMinimumLevel(LogLevel.Trace);
            b.AddZLoggerLogProcessor((_, _) => processor);
        });

        return (factory, delivered, processor);
    }

    private static async Task<List<LogEvent>> WaitFor(List<LogEvent> delivered, int count)
    {
        for (var i = 0; i < 100; i++)
        {
            lock (delivered)
            {
                if (delivered.Count >= count)
                    return [.. delivered];
            }

            await Task.Delay(20);
        }

        lock (delivered)
            return [.. delivered];
    }

    [Fact]
    public async Task An_error_sends_the_trail_first_in_order_tagged_without_payload_content()
    {
        var (factory, delivered, _) = Build();
        var logger = factory.CreateLogger("Orders.Service");

        using (new Activity("request").Start())
        {
            RetroLogging.Begin();

            using (logger.EnterMethod("PlaceOrder"))
            {
                logger.Inspect("orderId", 42);
                logger.LogSql("Query", "select * from orders");
                logger.LogError("boom");
            }
        }

        var events = await WaitFor(delivered, 4);

        events.Select(e => e.Title).ShouldBe(["Entering PlaceOrder", "orderId = 42", "Query", "boom"]);
        events.Take(3).ShouldAllBe(e => e.Tag == "Retro" && e.Payload == null && e.Type == 0);
        events[0].Nesting.ShouldBe(1);
        events[3].Level.ShouldBe((int)LogLevel.Error);

        factory.Dispose();
    }

    [Fact]
    public async Task A_unit_of_work_that_succeeds_sends_nothing_extra()
    {
        var (factory, delivered, _) = Build();
        var logger = factory.CreateLogger("Orders.Service");

        using (new Activity("request").Start())
        {
            RetroLogging.Begin();
            logger.LogDebug("step");
            logger.LogWarning("live");
        }

        (await WaitFor(delivered, 1)).ShouldHaveSingleItem().Title.ShouldBe("live");
        await Task.Delay(100);
        lock (delivered)
            delivered.Count.ShouldBe(1);

        factory.Dispose();
    }

    [Fact]
    public async Task Units_of_work_keep_their_own_trails()
    {
        var (factory, delivered, _) = Build();
        var logger = factory.CreateLogger("Orders.Service");

        using (new Activity("a").Start())
        {
            RetroLogging.Begin();
            logger.LogDebug("from a");
        }

        using (new Activity("b").Start())
        {
            RetroLogging.Begin();
            logger.LogDebug("from b");
            logger.LogError("b failed");
        }

        (await WaitFor(delivered, 2)).Select(e => e.Title).ShouldBe(["from b", "b failed"]);

        factory.Dispose();
    }

    [Fact]
    public async Task A_probed_unit_of_work_is_sent_live_not_kept()
    {
        var (factory, delivered, _) = Build();
        var logger = factory.CreateLogger("Orders.Service");

        using (new Activity("request").Start())
        {
            RetroLogging.Begin();
            DiagnosticProbe.Begin();
            logger.LogDebug("step");
        }

        (await WaitFor(delivered, 1)).ShouldHaveSingleItem().Tag.ShouldBe(DiagnosticProbe.Tag);

        factory.Dispose();
    }

    [Fact]
    public async Task Only_the_newest_events_of_a_trail_are_kept_and_the_rest_counted()
    {
        var (factory, delivered, processor) = Build(new RetroOptions { MaxEventsPerUnit = 2 });
        var logger = factory.CreateLogger("Orders.Service");

        using (new Activity("request").Start())
        {
            RetroLogging.Begin();
            logger.LogDebug("one");
            logger.LogDebug("two");
            logger.LogDebug("three");
            logger.LogError("boom");
        }

        (await WaitFor(delivered, 3)).Select(e => e.Title).ShouldBe(["two", "three", "boom"]);
        processor.RetroDroppedEventCount.ShouldBe(1);

        factory.Dispose();
    }
}

public class RetroBuffersTests
{
    private static LogEvent Event(string title) => new() { Title = title };

    [Fact]
    public void At_the_cap_on_trails_a_new_unit_of_work_keeps_nothing()
    {
        using var buffers = new RetroBuffers(new RetroOptions { MaxUnits = 1 });

        buffers.Hold("a", Event("a1"));
        buffers.Hold("b", Event("b1"));

        buffers.Release("b").ShouldBeEmpty();
        buffers.Release("a").Select(e => e.Title).ShouldBe(["a1"]);
        buffers.DroppedCount.ShouldBe(1);
    }

    [Fact]
    public void Release_frees_a_place_for_another_unit_of_work()
    {
        using var buffers = new RetroBuffers(new RetroOptions { MaxUnits = 1 });

        buffers.Hold("a", Event("a1"));
        buffers.Release("a");
        buffers.Hold("b", Event("b1"));

        buffers.Release("b").Select(e => e.Title).ShouldBe(["b1"]);
        buffers.Count.ShouldBe(0);
    }

    [Fact]
    public void An_idle_trail_is_discarded_and_counted()
    {
        using var buffers = new RetroBuffers(new RetroOptions { IdleTimeout = TimeSpan.FromSeconds(1) });

        buffers.Hold("a", Event("a1"));
        buffers.Hold("a", Event("a2"));
        buffers.Sweep(Environment.TickCount64 + 60_000);

        buffers.Count.ShouldBe(0);
        buffers.Release("a").ShouldBeEmpty();
        buffers.DroppedCount.ShouldBe(2);
    }

    [Fact]
    public void A_recent_trail_survives_the_sweep()
    {
        using var buffers = new RetroBuffers(new RetroOptions());

        buffers.Hold("a", Event("a1"));
        buffers.Sweep(Environment.TickCount64);

        buffers.Release("a").Select(e => e.Title).ShouldBe(["a1"]);
    }

    [Fact]
    public void Out_of_range_limits_are_clamped()
    {
        using var buffers = new RetroBuffers(new RetroOptions { MaxEventsPerUnit = 0, MaxUnits = -5, IdleTimeout = TimeSpan.Zero });

        buffers.Hold("a", Event("a1"));
        buffers.Hold("a", Event("a2"));

        buffers.Release("a").Select(e => e.Title).ShouldBe(["a2"]);
    }
}
