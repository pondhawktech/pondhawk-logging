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
/// Retro logging in the processor: Debug below the switch, in a marked unit of work or under a switch with Retro, is kept
/// as a trail, and its last lines go in the payload of the next Error in the same unit of work. The level gate is the
/// filter's job (<see cref="AddWatchTests"/>); here the floor is open, as the filter leaves it for retro logging.
/// </summary>
public class RetroLoggingTests
{
    private static (ILoggerFactory Factory, List<LogEvent> Delivered, WatchLoggerProcessor Processor) Build(
        RetroOptions retro = null, bool switchRetro = false)
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
        if (switchRetro)
            switches.WhenMatched("Orders", "", LogLevel.Warning, System.Drawing.Color.White, retro: true);

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

    // The lines of the payload's Trail block, without the offsets: "<segment> <indent><text>".
    private static List<string> TrailOf(LogEvent e)
    {
        var lines = e.Payload!.Split('\n').Select(l => l.TrimEnd('\r')).ToList();
        var start = lines.FindIndex(l => l.StartsWith("--- Trail (", StringComparison.Ordinal));
        if (start < 0)
            return [];

        return [.. lines.Skip(start + 1).TakeWhile(l => l.StartsWith('+')).Select(l => l[(l.IndexOf("s  ", StringComparison.Ordinal) + 3)..].TrimEnd())];
    }

    private static int At(LogEvent e, string banner) => e.Payload!.IndexOf(banner, StringComparison.Ordinal);

    [Fact]
    public async Task An_error_carries_the_trail_then_the_exception_in_one_text_payload_and_nothing_else_is_sent()
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
                logger.LogError(new InvalidOperationException("no stock"), "boom");
            }
        }

        var error = (await WaitFor(delivered, 1)).ShouldHaveSingleItem();
        await Task.Delay(100);
        lock (delivered)
            delivered.Count.ShouldBe(1);

        error.Title.ShouldBe("boom");
        error.Type.ShouldBe(LogStateContract.TextPayload);
        error.Payload.ShouldStartWith("--- Trail (3 lines) ---");
        At(error, "--- Exception ---").ShouldBeGreaterThan(0);
        error.Payload.ShouldContain("no stock");
        error.Payload.ShouldNotContain("select * from orders");   // a payload's title only
        TrailOf(error).ShouldBe(["Service  Entering PlaceOrder", "Service    orderId = 42", "Service    Query"]);

        factory.Dispose();
    }

    [Fact]
    public async Task With_context_the_order_is_context_trail_exception()
    {
        var (factory, delivered, _) = Build();
        var logger = factory.CreateLogger("Orders.Service");

        using (new Activity("request").Start())
        {
            RetroLogging.Begin();
            logger.LogDebug("step");
            logger.ErrorWithContext(new InvalidOperationException("no stock"), new { OrderId = 42 }, "boom");
        }

        var error = (await WaitFor(delivered, 1)).ShouldHaveSingleItem();
        error.Type.ShouldBe(LogStateContract.TextPayload);
        At(error, "--- Context ---").ShouldBe(0);
        At(error, "--- Trail (1 line) ---").ShouldBeGreaterThan(At(error, "\"OrderId\""));
        At(error, "--- Exception ---").ShouldBeGreaterThan(At(error, "--- Trail"));

        factory.Dispose();
    }

    [Fact]
    public async Task Context_alone_becomes_text_context_then_trail()
    {
        var (factory, delivered, _) = Build();
        var logger = factory.CreateLogger("Orders.Service");

        using (new Activity("request").Start())
        {
            RetroLogging.Begin();
            logger.LogDebug("step");
            logger.ErrorWithContext(new { OrderId = 42 }, "boom");
        }

        var error = (await WaitFor(delivered, 1)).ShouldHaveSingleItem();
        error.Type.ShouldBe(LogStateContract.TextPayload);
        At(error, "--- Context ---").ShouldBe(0);
        At(error, "--- Trail (1 line) ---").ShouldBeGreaterThan(0);
        error.Payload.ShouldNotContain("--- Exception ---");

        factory.Dispose();
    }

    [Fact]
    public async Task An_error_with_neither_carries_the_trail_alone()
    {
        var (factory, delivered, _) = Build();
        var logger = factory.CreateLogger("Orders.Service");

        using (new Activity("request").Start())
        {
            RetroLogging.Begin();
            logger.LogDebug("step");
            logger.LogCritical("boom");
        }

        var error = (await WaitFor(delivered, 1)).ShouldHaveSingleItem();
        error.Type.ShouldBe(LogStateContract.TextPayload);
        error.Payload.ShouldStartWith("--- Trail (1 line) ---");
        TrailOf(error).ShouldBe(["Service  step"]);

        factory.Dispose();
    }

    [Fact]
    public async Task An_error_with_its_own_payload_keeps_it_and_its_trail_is_discarded()
    {
        var (factory, delivered, _) = Build();
        var logger = factory.CreateLogger("Orders.Service");

        using (new Activity("request").Start())
        {
            RetroLogging.Begin();
            logger.LogDebug("step");
            logger.LogJson(LogLevel.Error, "Order", "{\"id\":1}");
            logger.LogError("again");
        }

        var events = await WaitFor(delivered, 2);
        events[0].Payload.ShouldBe("{\"id\":1}");
        events[0].Type.ShouldBe(LogStateContract.JsonPayload);
        events[1].Payload.ShouldBeNull();   // the discarded trail does not come back

        factory.Dispose();
    }

    [Fact]
    public async Task A_second_error_carries_only_the_lines_since_the_first()
    {
        var (factory, delivered, _) = Build();
        var logger = factory.CreateLogger("Orders.Service");

        using (new Activity("request").Start())
        {
            RetroLogging.Begin();
            logger.LogDebug("one");
            logger.LogError("first");
            logger.LogDebug("two");
            logger.LogError("second");
        }

        var events = await WaitFor(delivered, 2);
        TrailOf(events[0]).ShouldBe(["Service  one"]);
        TrailOf(events[1]).ShouldBe(["Service  two"]);

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

        TrailOf((await WaitFor(delivered, 1)).ShouldHaveSingleItem()).ShouldBe(["Service  from b"]);

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

        TrailOf((await WaitFor(delivered, 1)).ShouldHaveSingleItem()).ShouldBe(["Service  in the request", "Service  in the query"]);

        factory.Dispose();
    }

    [Fact]
    public async Task Under_a_switch_with_retro_a_child_activitys_lines_go_on_the_request()
    {
        var (factory, delivered, _) = Build(switchRetro: true);
        var logger = factory.CreateLogger("Orders.Service");

        using (new Activity("request").Start())
        {
            logger.LogDebug("in the request");

            using (new Activity("outgoing call").Start())
                logger.LogDebug("in the call");   // the child ends here; its lines are on the request

            logger.LogError("boom");
        }

        TrailOf((await WaitFor(delivered, 1)).ShouldHaveSingleItem()).ShouldBe(["Service  in the request", "Service  in the call"]);

        factory.Dispose();
    }

    [Fact]
    public async Task A_marked_activity_holds_the_trail_rather_than_the_request()
    {
        var (factory, delivered, _) = Build(switchRetro: true);
        var logger = factory.CreateLogger("Orders.Service");

        using (new Activity("request").Start())
        {
            using (new Activity("job").Start())
            {
                RetroLogging.Begin();
                logger.LogDebug("in the job");
            }

            logger.LogError("after the job");   // the job's trail went with it
        }

        (await WaitFor(delivered, 1)).ShouldHaveSingleItem().Payload.ShouldBeNull();

        factory.Dispose();
    }

    [Fact]
    public async Task Under_a_switch_with_retro_and_no_activity_nothing_is_kept()
    {
        var (factory, delivered, _) = Build(switchRetro: true);
        var logger = factory.CreateLogger("Orders.Service");

        Activity.Current.ShouldBeNull();
        logger.LogDebug("no unit of work");   // not kept: the floor is open here, so it goes live (the filter stops it)
        logger.LogError("boom");

        var events = await WaitFor(delivered, 2);
        events.Select(e => e.Title).ShouldBe(["no unit of work", "boom"]);
        events[1].Payload.ShouldBeNull();

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

        TrailOf((await WaitFor(delivered, 1)).ShouldHaveSingleItem()).ShouldBe(["Service  step"]);

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
    public async Task Only_the_newest_lines_of_a_trail_are_kept_and_the_rest_counted()
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

        var error = (await WaitFor(delivered, 1)).ShouldHaveSingleItem();
        error.Payload.ShouldStartWith("--- Trail (2 lines, 1 dropped) ---");
        TrailOf(error).ShouldBe(["Service  two", "Service  three"]);
        processor.RetroDroppedEventCount.ShouldBe(1);

        factory.Dispose();
    }

    [Fact]
    public void The_default_trail_is_twenty_lines()
    {
        new RetroOptions().MaxEventsPerUnit.ShouldBe(20);
    }
}

public class RetroTrailsTests
{
    private static TrailLine Line(string text, int nesting = 0, long timestamp = 0, string category = "Service") =>
        new(timestamp, category, nesting, text);

    private static ReleasedTrail Trail(params TrailLine[] lines) => new(lines, 0);

    private static string[] Lines(string block) => block.Split('\n').Select(l => l.TrimEnd('\r')).ToArray();

    [Fact]
    public void A_trail_is_released_once_and_a_later_line_starts_a_new_one()
    {
        var trails = new RetroTrails(new RetroOptions());
        using var request = new Activity("request").Start();

        trails.Hold(request, Line("one"));
        RetroTrails.Release(request).Lines.Select(l => l.Text).ShouldBe(["one"]);
        RetroTrails.Release(request).Lines.ShouldBeEmpty();

        trails.Hold(request, Line("two"));
        RetroTrails.Release(request).Lines.Select(l => l.Text).ShouldBe(["two"]);
    }

    [Fact]
    public void Each_activity_has_its_own_trail()
    {
        var trails = new RetroTrails(new RetroOptions());
        using var a = new Activity("a").Start();
        trails.Hold(a, Line("a1"));
        using var b = new Activity("b").Start();
        trails.Hold(b, Line("b1"));

        RetroTrails.Release(b).Lines.Select(l => l.Text).ShouldBe(["b1"]);
        RetroTrails.Release(a).Lines.Select(l => l.Text).ShouldBe(["a1"]);
    }

    [Fact]
    public void A_trail_is_neither_baggage_nor_a_tag()
    {
        var trails = new RetroTrails(new RetroOptions());
        using var request = new Activity("request").Start();

        trails.Hold(request, Line("one"));

        request.Baggage.ShouldBeEmpty();
        request.TagObjects.ShouldBeEmpty();
    }

    [Fact]
    public void Out_of_range_limits_are_clamped()
    {
        var trails = new RetroTrails(new RetroOptions { MaxEventsPerUnit = 0 });
        using var request = new Activity("request").Start();

        trails.Hold(request, Line("a1"));
        trails.Hold(request, Line("a2"));

        var released = RetroTrails.Release(request);
        released.Lines.Select(l => l.Text).ShouldBe(["a2"]);
        released.Dropped.ShouldBe(1);
        trails.DroppedCount.ShouldBe(1);
    }

    [Fact]
    public void The_outermost_activity_is_the_request_not_a_child()
    {
        using var request = new Activity("request").Start();
        using var call = new Activity("call").Start();

        RetroTrails.Outermost(call).ShouldBeSameAs(request);
        RetroTrails.Outermost(request).ShouldBeSameAs(request);
        RetroTrails.Outermost(null).ShouldBeNull();
    }

    [Theory]
    [InlineData("Shop.Orders.OrderService", "OrderService")]
    [InlineData("OrderService", "OrderService")]
    [InlineData("Shop.", "Shop.")]
    public void A_category_shows_its_last_segment(string category, string shown)
    {
        RetroTrails.LastSegment(category).ShouldBe(shown);
    }

    [Fact]
    public void Text_is_capped_at_500_characters()
    {
        RetroTrails.Cap(new string('x', 500)).Length.ShouldBe(500);
        var cut = RetroTrails.Cap(new string('x', 2000));
        cut.Length.ShouldBe(500);
        cut.ShouldEndWith("…");
    }

    [Fact]
    public void Lines_show_offsets_from_the_first_line()
    {
        var second = Stopwatch.Frequency + Stopwatch.Frequency / 4;   // 1.25 s later

        var block = Lines(RetroTrails.Format(Trail(Line("first", timestamp: 0), Line("second", timestamp: second))));

        block[0].ShouldBe("--- Trail (2 lines) " + new string('-', 33));
        block[1].ShouldBe("+0.000s  Service  first");
        block[2].ShouldBe("+1.250s  Service  second");
    }

    [Fact]
    public void Categories_are_padded_to_the_longest()
    {
        var block = Lines(RetroTrails.Format(Trail(Line("a", category: "Service"), Line("b", category: "Repository"))));

        block[1].ShouldBe("+0.000s  Service     a");
        block[2].ShouldBe("+0.000s  Repository  b");
    }

    [Fact]
    public void Nesting_indents_two_spaces_per_depth_and_an_exit_sits_at_its_enters_depth()
    {
        var block = Lines(RetroTrails.Format(Trail(
            Line("Entering PlaceOrder", nesting: 1),
            Line("subtotal = 30"),
            Line("Entering Load", nesting: 1),
            Line("Query"),
            Line("Exiting Load", nesting: -1),
            Line("Exiting PlaceOrder", nesting: -1))));

        block.Skip(1).Select(l => l[9..]).ShouldBe([
            "Service  Entering PlaceOrder",
            "Service    subtotal = 30",
            "Service    Entering Load",
            "Service      Query",
            "Service    Exiting Load",
            "Service  Exiting PlaceOrder",
        ]);
    }

    [Fact]
    public void A_trail_that_starts_inside_a_method_never_goes_below_depth_zero()
    {
        var block = Lines(RetroTrails.Format(Trail(Line("Query"), Line("Exiting Load", nesting: -1), Line("next"))));

        block.Skip(1).Select(l => l[9..]).ShouldBe(["Service  Query", "Service  Exiting Load", "Service  next"]);
    }
}
