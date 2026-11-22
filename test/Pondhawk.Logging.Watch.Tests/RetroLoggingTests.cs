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
    public async Task Work_inside_a_child_activity_joins_the_marked_unit_of_works_trail()
    {
        var (factory, delivered, _) = Build();
        var logger = factory.CreateLogger("Orders.Service");

        using (new Activity("request").Start())
        {
            RetroLogging.Begin();
            logger.LogDebug("in the request");

            using (new Activity("query").Start())
                logger.LogDebug("in the query");

            logger.LogError("boom");
        }

        (await WaitFor(delivered, 3)).Select(e => e.Title).ShouldBe(["in the request", "in the query", "boom"]);

        factory.Dispose();
    }

    [Fact]
    public async Task Units_of_work_that_succeeded_never_crowd_out_one_that_fails()
    {
        var (factory, delivered, _) = Build();
        var logger = factory.CreateLogger("Orders.Service");

        // 5.0.1 kept these trails in a table capped at 256 until an idle timeout, so the failure below kept nothing.
        for (var i = 0; i < 1000; i++)
        {
            using (new Activity("succeeds").Start())
            {
                RetroLogging.Begin();
                logger.LogDebug("quiet step");
            }
        }

        using (new Activity("fails").Start())
        {
            RetroLogging.Begin();
            logger.LogDebug("step");
            logger.LogError("boom");
        }

        (await WaitFor(delivered, 2)).Select(e => e.Title).ShouldBe(["step", "boom"]);

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

public class RetroTrailsTests
{
    private static LogEvent Event(string title) => new() { Title = title };

    [Fact]
    public void A_trail_is_released_once_and_a_later_event_starts_a_new_one()
    {
        var trails = new RetroTrails(new RetroOptions());
        using var request = new Activity("request").Start();

        trails.Hold(request, Event("one"));
        RetroTrails.Release(request).Select(e => e.Title).ShouldBe(["one"]);
        RetroTrails.Release(request).ShouldBeEmpty();

        trails.Hold(request, Event("two"));
        RetroTrails.Release(request).Select(e => e.Title).ShouldBe(["two"]);
    }

    [Fact]
    public void Each_activity_has_its_own_trail()
    {
        var trails = new RetroTrails(new RetroOptions());
        using var a = new Activity("a").Start();
        trails.Hold(a, Event("a1"));
        using var b = new Activity("b").Start();
        trails.Hold(b, Event("b1"));

        RetroTrails.Release(b).Select(e => e.Title).ShouldBe(["b1"]);
        RetroTrails.Release(a).Select(e => e.Title).ShouldBe(["a1"]);
    }

    [Fact]
    public void A_trail_is_neither_baggage_nor_a_tag()
    {
        var trails = new RetroTrails(new RetroOptions());
        using var request = new Activity("request").Start();

        trails.Hold(request, Event("one"));

        request.Baggage.ShouldBeEmpty();
        request.TagObjects.ShouldBeEmpty();
    }

    [Fact]
    public void Out_of_range_limits_are_clamped()
    {
        var trails = new RetroTrails(new RetroOptions { MaxEventsPerUnit = 0 });
        using var request = new Activity("request").Start();

        trails.Hold(request, Event("a1"));
        trails.Hold(request, Event("a2"));

        RetroTrails.Release(request).Select(e => e.Title).ShouldBe(["a2"]);
        trails.DroppedCount.ShouldBe(1);
    }
}
