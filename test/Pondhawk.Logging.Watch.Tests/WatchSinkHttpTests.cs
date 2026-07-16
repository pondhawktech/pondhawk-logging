using System.Net;
using Pondhawk.Logging.Watch.Tests.Http;
using Serilog.Events;
using Shouldly;
using Xunit;
using SerilogEvent = Serilog.Events.LogEvent;

namespace Pondhawk.Logging.Watch.Tests;

public class WatchSinkHttpTests
{

    private static HttpClient CreateClient(MockHttpHandler handler)
    {
        return new HttpClient(handler) { BaseAddress = new Uri("http://localhost:11000") };
    }

    private static SwitchSource CreateSwitchSource()
    {
        var source = new SwitchSource();
        source.WhenNotMatched(LogEventLevel.Verbose);
        return source;
    }

    private static SerilogEvent MakeSerilogEvent(string message, LogEventLevel level = LogEventLevel.Information)
    {
        return new SerilogEvent(
            DateTimeOffset.UtcNow,
            level,
            null,
            new Serilog.Parsing.MessageTemplateParser().Parse(message),
            []);
    }

    private static List<SerilogEvent> MakeSerilogEvents(string message, LogEventLevel level = LogEventLevel.Information)
    {
        return [MakeSerilogEvent(message, level)];
    }

    private static List<SerilogEvent> MakeCriticalSerilogEvents(string message)
    {
        return MakeSerilogEvents(message, LogEventLevel.Warning);
    }

    // --- Constructor ---

    [Fact]
    public void Constructor_NullClient_Throws()
    {
        Should.Throw<ArgumentNullException>(() => new WatchSink(null, CreateSwitchSource(), "domain"));
    }

    [Fact]
    public void Constructor_NullSwitchSource_Throws()
    {
        var handler = new MockHttpHandler();
        var client = CreateClient(handler);

        Should.Throw<ArgumentNullException>(() => new WatchSink(client, null, "domain"));
    }

    // --- Defaults ---

    [Fact]
    public void DefaultProperties_HaveExpectedValues()
    {
        var handler = new MockHttpHandler();
        var client = CreateClient(handler);
        var sink = new WatchSink(client, CreateSwitchSource(), "test");

        sink.FailureThreshold.ShouldBe(3);
        sink.BaseRetryDelay.ShouldBe(TimeSpan.FromSeconds(5));
        sink.MaxRetryDelay.ShouldBe(TimeSpan.FromMinutes(2));
        sink.MaxCriticalBufferSize.ShouldBe(1000);
        sink.IsCircuitOpen.ShouldBeFalse();
        sink.CriticalBufferCount.ShouldBe(0);
        sink.DroppedEventCount.ShouldBe(0);
    }

    // --- Successful send ---

    [Fact]
    public async Task FlushBatch_SuccessfulSend_MakesHttpPost()
    {
        var handler = new MockHttpHandler();
        handler.RespondWith(HttpStatusCode.OK);
        var sink = new WatchSink(CreateClient(handler), CreateSwitchSource(), "my-domain");

        await sink.FlushBatchAsync(MakeSerilogEvents("hello"));

        handler.Requests.Count.ShouldBe(1);
        handler.Requests[0].Method.ShouldBe(HttpMethod.Post);
        handler.Requests[0].RequestUri.PathAndQuery.ShouldBe("/api/sink");
    }

    [Fact]
    public async Task FlushBatch_SuccessfulSend_SetsContentTypeAndDomainHeader()
    {
        var handler = new MockHttpHandler();
        handler.RespondWith(HttpStatusCode.OK);
        var sink = new WatchSink(CreateClient(handler), CreateSwitchSource(), "my-domain");

        await sink.FlushBatchAsync(MakeSerilogEvents("hello"));

        var request = handler.Requests[0];
        request.Content.Headers.ContentType.MediaType.ShouldBe(LogEventBatchSerializer.ContentType);
        request.Content.Headers.GetValues("X-Domain").ShouldContain("my-domain");
    }

    [Fact]
    public async Task FlushBatch_SuccessfulSend_CircuitStaysClosed()
    {
        var handler = new MockHttpHandler();
        handler.RespondWith(HttpStatusCode.OK);
        var sink = new WatchSink(CreateClient(handler), CreateSwitchSource(), "test");

        await sink.FlushBatchAsync(MakeSerilogEvents("e1"));

        sink.IsCircuitOpen.ShouldBeFalse();
    }

    // --- Circuit breaker ---

    [Fact]
    public async Task FlushBatch_FailuresBelowThreshold_CircuitStaysClosed()
    {
        var handler = new MockHttpHandler();
        handler.RespondWith(HttpStatusCode.InternalServerError);
        var sink = new WatchSink(CreateClient(handler), CreateSwitchSource(), "test")
        {
            FailureThreshold = 3
        };

        await sink.FlushBatchAsync(MakeSerilogEvents("e1"));
        await sink.FlushBatchAsync(MakeSerilogEvents("e2"));

        sink.IsCircuitOpen.ShouldBeFalse();
    }

    [Fact]
    public async Task FlushBatch_FailuresAtThreshold_CircuitOpens()
    {
        var handler = new MockHttpHandler();
        handler.RespondWith(HttpStatusCode.InternalServerError);
        var sink = new WatchSink(CreateClient(handler), CreateSwitchSource(), "test")
        {
            FailureThreshold = 3,
            BaseRetryDelay = TimeSpan.FromMinutes(5) // long enough to stay open
        };

        for (var i = 0; i < 3; i++)
            await sink.FlushBatchAsync(MakeSerilogEvents($"e{i}"));

        sink.IsCircuitOpen.ShouldBeTrue();
    }

    [Fact]
    public async Task FlushBatch_ExceptionOnSend_CountsAsFailure()
    {
        var handler = new MockHttpHandler();
        handler.ThrowOnSend(new HttpRequestException("connection refused"));
        var sink = new WatchSink(CreateClient(handler), CreateSwitchSource(), "test")
        {
            FailureThreshold = 1,
            BaseRetryDelay = TimeSpan.FromMinutes(5)
        };

        await sink.FlushBatchAsync(MakeSerilogEvents("e1"));

        sink.IsCircuitOpen.ShouldBeTrue();
    }

    [Fact]
    public async Task FlushBatch_SuccessAfterFailures_ResetsCircuit()
    {
        var callCount = 0;
        var handler = new MockHttpHandler();
        handler.SetHandler((_, _) =>
        {
            callCount++;
            // First 2 calls fail, third succeeds
            if (callCount <= 2)
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        });

        var sink = new WatchSink(CreateClient(handler), CreateSwitchSource(), "test")
        {
            FailureThreshold = 5 // high enough so circuit doesn't open
        };

        await sink.FlushBatchAsync(MakeSerilogEvents("fail1"));
        await sink.FlushBatchAsync(MakeSerilogEvents("fail2"));
        await sink.FlushBatchAsync(MakeSerilogEvents("success"));

        sink.IsCircuitOpen.ShouldBeFalse();
    }

    // --- Circuit open: skips HTTP call ---

    [Fact]
    public async Task FlushBatch_CircuitOpen_SkipsHttpCall()
    {
        var handler = new MockHttpHandler();
        handler.RespondWith(HttpStatusCode.InternalServerError);
        var sink = new WatchSink(CreateClient(handler), CreateSwitchSource(), "test")
        {
            FailureThreshold = 1,
            BaseRetryDelay = TimeSpan.FromMinutes(5)
        };

        // First call fails and opens circuit
        await sink.FlushBatchAsync(MakeSerilogEvents("fail"));
        var requestsAfterOpen = handler.Requests.Count;

        // Second call should be skipped
        await sink.FlushBatchAsync(MakeSerilogEvents("skipped"));

        handler.Requests.Count.ShouldBe(requestsAfterOpen);
    }

    // --- Critical event buffering ---

    [Fact]
    public async Task FlushBatch_CircuitOpen_BuffersCriticalEvents()
    {
        var handler = new MockHttpHandler();
        handler.RespondWith(HttpStatusCode.InternalServerError);
        var sink = new WatchSink(CreateClient(handler), CreateSwitchSource(), "test")
        {
            FailureThreshold = 1,
            BaseRetryDelay = TimeSpan.FromMinutes(5)
        };

        // Open circuit
        await sink.FlushBatchAsync(MakeSerilogEvents("fail"));
        sink.IsCircuitOpen.ShouldBeTrue();

        // Send batch with critical event while circuit is open
        await sink.FlushBatchAsync(MakeCriticalSerilogEvents("critical1"));

        sink.CriticalBufferCount.ShouldBeGreaterThan(0);
    }

    [Fact]
    public async Task FlushBatch_CircuitOpen_DoesNotBufferNonCriticalEvents()
    {
        var handler = new MockHttpHandler();
        handler.RespondWith(HttpStatusCode.InternalServerError);
        var sink = new WatchSink(CreateClient(handler), CreateSwitchSource(), "test")
        {
            FailureThreshold = 1,
            BaseRetryDelay = TimeSpan.FromMinutes(5)
        };

        // Open circuit
        await sink.FlushBatchAsync(MakeSerilogEvents("fail"));

        // Clear any critical events buffered from the failure
        var bufferBefore = sink.CriticalBufferCount;

        // Send non-critical event (Debug level, below Warning)
        await sink.FlushBatchAsync(MakeSerilogEvents("debug", LogEventLevel.Debug));

        sink.CriticalBufferCount.ShouldBe(bufferBefore);
    }

    [Fact]
    public async Task FlushBatch_FailedBatch_BuffersCriticalEventsFromBatch()
    {
        var handler = new MockHttpHandler();
        handler.RespondWith(HttpStatusCode.InternalServerError);
        var sink = new WatchSink(CreateClient(handler), CreateSwitchSource(), "test")
        {
            FailureThreshold = 10 // don't open circuit
        };

        var events = new List<SerilogEvent>
        {
            MakeSerilogEvent("debug", LogEventLevel.Debug),
            MakeSerilogEvent("warning", LogEventLevel.Warning),
            MakeSerilogEvent("error", LogEventLevel.Error)
        };

        await sink.FlushBatchAsync(events);

        // Warning + Error events should be buffered
        sink.CriticalBufferCount.ShouldBe(2);
    }

    // --- Buffer overflow ---

    [Fact]
    public async Task FlushBatch_BufferOverflow_DropsOldestEvents()
    {
        var handler = new MockHttpHandler();
        handler.RespondWith(HttpStatusCode.InternalServerError);
        var sink = new WatchSink(CreateClient(handler), CreateSwitchSource(), "test")
        {
            FailureThreshold = 1,
            MaxCriticalBufferSize = 3,
            BaseRetryDelay = TimeSpan.FromMinutes(5)
        };

        // Open circuit
        await sink.FlushBatchAsync(MakeSerilogEvents("fail"));

        // Send 5 critical events — buffer max is 3
        for (var i = 0; i < 5; i++)
            await sink.FlushBatchAsync(MakeCriticalSerilogEvents($"crit{i}"));

        sink.CriticalBufferCount.ShouldBeLessThanOrEqualTo(3);
        sink.DroppedEventCount.ShouldBeGreaterThan(0);
    }

    // --- Flush critical buffer on success ---

    [Fact]
    public async Task FlushBatch_AfterCircuitCloses_FlushesCriticalBuffer()
    {
        var callCount = 0;
        var handler = new MockHttpHandler();
        handler.SetHandler((_, _) =>
        {
            callCount++;
            if (callCount <= 1)
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.InternalServerError));
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
        });

        var sink = new WatchSink(CreateClient(handler), CreateSwitchSource(), "test")
        {
            FailureThreshold = 1,
            BaseRetryDelay = TimeSpan.Zero // circuit closes immediately
        };

        // First call fails, buffers the critical event
        await sink.FlushBatchAsync(MakeCriticalSerilogEvents("buffered"));
        sink.CriticalBufferCount.ShouldBeGreaterThan(0);

        // Second call succeeds, flushes buffer
        await sink.FlushBatchAsync(MakeSerilogEvents("success"));

        sink.CriticalBufferCount.ShouldBe(0);
    }

}
