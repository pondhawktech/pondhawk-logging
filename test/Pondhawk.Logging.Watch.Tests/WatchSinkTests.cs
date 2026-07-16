using System.Diagnostics;
using System.Net;
using Pondhawk.Logging.Watch.Tests.Http;
using Serilog.Events;
using Shouldly;
using Xunit;
using SerilogEvent = Serilog.Events.LogEvent;

namespace Pondhawk.Logging.Watch.Tests;

public class WatchSinkTests
{
    private static readonly Serilog.Parsing.MessageTemplateParser Parser = new();

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

    private static SerilogEvent MakeSerilogEvent(
        LogEventLevel level = LogEventLevel.Information,
        List<LogEventProperty> properties = null,
        Exception exception = null,
        string sourceContext = null)
    {
        var props = new List<LogEventProperty>(properties ?? []);
        if (sourceContext is not null)
            props.Add(new LogEventProperty("SourceContext", new ScalarValue(sourceContext)));

        return new SerilogEvent(
            DateTimeOffset.UtcNow,
            level,
            exception,
            Parser.Parse("Test message"),
            props);
    }

    private static List<SerilogEvent> MakeEventList(
        LogEventLevel level = LogEventLevel.Information,
        List<LogEventProperty> properties = null,
        Exception exception = null,
        string sourceContext = null)
    {
        return [MakeSerilogEvent(level, properties, exception, sourceContext)];
    }

    // --- ConvertEvent: basic conversion ---

    [Fact]
    public async Task ConvertEvent_BasicConversion_MakesHttpRequest()
    {
        var handler = new MockHttpHandler();
        handler.RespondWith(HttpStatusCode.OK);
        var sink = new WatchSink(CreateClient(handler), CreateSwitchSource(), "test");

        await sink.FlushBatchAsync(MakeEventList(sourceContext: "MyApp.Service"));

        handler.Requests.Count.ShouldBe(1);
    }

    // --- ConvertEvent: level below threshold filters event ---

    [Fact]
    public async Task ConvertEvent_LevelBelowThreshold_FiltersEvent()
    {
        var handler = new MockHttpHandler();
        handler.RespondWith(HttpStatusCode.OK);
        var source = new SwitchSource();
        source.WhenNotMatched(LogEventLevel.Warning);
        var sink = new WatchSink(CreateClient(handler), source, "test");

        await sink.FlushBatchAsync(MakeEventList(level: LogEventLevel.Debug));

        handler.Requests.Count.ShouldBe(0);
    }

    // --- ConvertEvent: SourceContext extraction ---

    [Fact]
    public async Task ConvertEvent_WithSourceContext_UsesCategory()
    {
        var handler = new MockHttpHandler();
        handler.RespondWith(HttpStatusCode.OK);
        var sink = new WatchSink(CreateClient(handler), CreateSwitchSource(), "test");

        await sink.FlushBatchAsync(MakeEventList(sourceContext: "MyApp.Services.OrderService"));

        handler.Requests.Count.ShouldBe(1);
    }

    // --- ConvertEvent: Watch.Nesting property ---

    [Fact]
    public async Task ConvertEvent_WithNesting_Succeeds()
    {
        var handler = new MockHttpHandler();
        handler.RespondWith(HttpStatusCode.OK);
        var sink = new WatchSink(CreateClient(handler), CreateSwitchSource(), "test");

        var props = new List<LogEventProperty>
        {
            new(LogPropertyNames.Nesting, new ScalarValue(1))
        };
        await sink.FlushBatchAsync(MakeEventList(properties: props));

        handler.Requests.Count.ShouldBe(1);
    }

    // --- ConvertEvent: Watch.PayloadType + Watch.PayloadContent ---

    [Fact]
    public async Task ConvertEvent_WithPayloadTypeAndContent_Succeeds()
    {
        var handler = new MockHttpHandler();
        handler.RespondWith(HttpStatusCode.OK);
        var sink = new WatchSink(CreateClient(handler), CreateSwitchSource(), "test");

        var props = new List<LogEventProperty>
        {
            new(LogPropertyNames.PayloadType, new ScalarValue(1)),
            new(LogPropertyNames.PayloadContent, new ScalarValue("{\"key\":\"value\"}"))
        };
        await sink.FlushBatchAsync(MakeEventList(properties: props));

        handler.Requests.Count.ShouldBe(1);
    }

    // --- ConvertEvent: exception branch ---

    [Fact]
    public async Task ConvertEvent_WithException_Succeeds()
    {
        var handler = new MockHttpHandler();
        handler.RespondWith(HttpStatusCode.OK);
        var sink = new WatchSink(CreateClient(handler), CreateSwitchSource(), "test");

        await sink.FlushBatchAsync(MakeEventList(exception: new InvalidOperationException("boom")));

        handler.Requests.Count.ShouldBe(1);
    }

    // --- ConvertEvent: structured payload with ScalarValue properties ---

    [Fact]
    public async Task ConvertEvent_WithCustomProperties_BuildsStructuredPayload()
    {
        var handler = new MockHttpHandler();
        handler.RespondWith(HttpStatusCode.OK);
        var sink = new WatchSink(CreateClient(handler), CreateSwitchSource(), "test");

        var props = new List<LogEventProperty>
        {
            new("UserId", new ScalarValue(42)),
            new("UserName", new ScalarValue("John"))
        };
        await sink.FlushBatchAsync(MakeEventList(properties: props));

        handler.Requests.Count.ShouldBe(1);
    }

    // --- ConvertPropertyValue: SequenceValue ---

    [Fact]
    public async Task ConvertEvent_WithSequenceValue_Succeeds()
    {
        var handler = new MockHttpHandler();
        handler.RespondWith(HttpStatusCode.OK);
        var sink = new WatchSink(CreateClient(handler), CreateSwitchSource(), "test");

        var props = new List<LogEventProperty>
        {
            new("Tags", new SequenceValue([new ScalarValue(1), new ScalarValue(2), new ScalarValue(3)]))
        };
        await sink.FlushBatchAsync(MakeEventList(properties: props));

        handler.Requests.Count.ShouldBe(1);
    }

    // --- ConvertPropertyValue: StructureValue ---

    [Fact]
    public async Task ConvertEvent_WithStructureValue_Succeeds()
    {
        var handler = new MockHttpHandler();
        handler.RespondWith(HttpStatusCode.OK);
        var sink = new WatchSink(CreateClient(handler), CreateSwitchSource(), "test");

        var props = new List<LogEventProperty>
        {
            new("Address", new StructureValue([
                new LogEventProperty("Street", new ScalarValue("123 Main")),
                new LogEventProperty("City", new ScalarValue("Springfield"))
            ]))
        };
        await sink.FlushBatchAsync(MakeEventList(properties: props));

        handler.Requests.Count.ShouldBe(1);
    }

    // --- ConvertPropertyValue: DictionaryValue ---

    [Fact]
    public async Task ConvertEvent_WithDictionaryValue_Succeeds()
    {
        var handler = new MockHttpHandler();
        handler.RespondWith(HttpStatusCode.OK);
        var sink = new WatchSink(CreateClient(handler), CreateSwitchSource(), "test");

        var props = new List<LogEventProperty>
        {
            new("Headers", new DictionaryValue([
                new KeyValuePair<ScalarValue, LogEventPropertyValue>(
                    new ScalarValue("Content-Type"), new ScalarValue("application/json")),
                new KeyValuePair<ScalarValue, LogEventPropertyValue>(
                    new ScalarValue("Accept"), new ScalarValue("text/html"))
            ]))
        };
        await sink.FlushBatchAsync(MakeEventList(properties: props));

        handler.Requests.Count.ShouldBe(1);
    }

    // --- Emit + Dispose integration ---

    [Fact]
    public void Emit_ThenDispose_FlushesEvent()
    {
        var handler = new MockHttpHandler();
        handler.RespondWith(HttpStatusCode.OK);
        var sink = new WatchSink(CreateClient(handler), CreateSwitchSource(), "test",
            flushInterval: TimeSpan.FromMilliseconds(10));

        sink.Emit(MakeSerilogEvent(sourceContext: "MyApp"));
        sink.Dispose();

        handler.Requests.Count.ShouldBeGreaterThan(0);
    }

    // --- DisposeAsync ---

#if NET10_0_OR_GREATER
    [Fact]
    public async Task DisposeAsync_Succeeds()
    {
        var handler = new MockHttpHandler();
        var sink = new WatchSink(CreateClient(handler), CreateSwitchSource(), "test");

        await sink.DisposeAsync();
    }
#endif

    // --- Dispose is idempotent ---

    [Fact]
    public void Dispose_IsIdempotent()
    {
        var handler = new MockHttpHandler();
        var sink = new WatchSink(CreateClient(handler), CreateSwitchSource(), "test");

        sink.Dispose();
        sink.Dispose();
    }

    // --- Emit after Dispose is no-op ---

    [Fact]
    public void Emit_AfterDispose_IsNoOp()
    {
        var handler = new MockHttpHandler();
        handler.RespondWith(HttpStatusCode.OK);
        var sink = new WatchSink(CreateClient(handler), CreateSwitchSource(), "test");

        sink.Dispose();
        sink.Emit(MakeSerilogEvent(sourceContext: "MyApp"));

        handler.Requests.Count.ShouldBe(0);
    }

    // --- GetCorrelationId: with Activity baggage ---

    [Fact]
    public async Task GetCorrelationId_WithActivityBaggage_UsesExistingCorrelation()
    {
        var handler = new MockHttpHandler();
        handler.RespondWith(HttpStatusCode.OK);
        var sink = new WatchSink(CreateClient(handler), CreateSwitchSource(), "test");

        using var activity = new Activity("TestCorrelation");
        activity.SetBaggage(LogPropertyNames.CorrelationBaggageKey, "test-correlation-123");
        activity.Start();

        await sink.FlushBatchAsync(MakeEventList(sourceContext: "MyApp"));

        handler.Requests.Count.ShouldBe(1);
    }

    // --- GetCorrelationId: Activity without existing correlation ---

    [Fact]
    public void GetCorrelationId_WithActivityWithoutBaggage_CreatesNewCorrelation()
    {
        var handler = new MockHttpHandler();
        handler.RespondWith(HttpStatusCode.OK);
        var sink = new WatchSink(CreateClient(handler), CreateSwitchSource(), "test",
            flushInterval: TimeSpan.FromMilliseconds(10));

        using var activity = new Activity("TestActivity");
        activity.Start();

        sink.Emit(MakeSerilogEvent(sourceContext: "MyApp"));

        var correlation = activity.GetBaggageItem(LogPropertyNames.CorrelationBaggageKey);
        correlation.ShouldNotBeNullOrEmpty();

        sink.Dispose();
    }

    // --- ConvertEvent: exception detail reaches the wire ---

    [Fact]
    public async Task ConvertEvent_WithException_TransmitsTypeMessageAndStackTrace()
    {
        var handler = new MockHttpHandler();
        LogEventBatch received = null;
        handler.SetHandler(async (req, ct) =>
        {
            var bytes = await req.Content.ReadAsByteArrayAsync(ct);
            using var ms = new MemoryStream(bytes);
            received = await LogEventBatchSerializer.FromStream(ms);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        var sink = new WatchSink(CreateClient(handler), CreateSwitchSource(), "test");

        Exception thrown;
        try { throw new InvalidOperationException("boom-detail"); }
        catch (Exception e) { thrown = e; }

        await sink.FlushBatchAsync(MakeEventList(level: LogEventLevel.Error, exception: thrown));

        received.ShouldNotBeNull();
        var ev = received.Events[0];
        ev.ErrorType.ShouldBe("System.InvalidOperationException");
        ev.Type.ShouldBe((int)PayloadType.Text);
        ev.Payload.ShouldContain("boom-detail");
        // The stack trace of the thrown exception names this method — proof it reached the wire.
        ev.Payload.ShouldContain(nameof(ConvertEvent_WithException_TransmitsTypeMessageAndStackTrace));
    }

    // --- FlushBatch: a poison event is skipped, not fatal ---

    [Fact]
    public async Task FlushBatch_PoisonEvent_SkippedButGoodEventsStillSent()
    {
        var handler = new MockHttpHandler();
        LogEventBatch received = null;
        handler.SetHandler(async (req, ct) =>
        {
            var bytes = await req.Content.ReadAsByteArrayAsync(ct);
            using var ms = new MemoryStream(bytes);
            received = await LogEventBatchSerializer.FromStream(ms);
            return new HttpResponseMessage(HttpStatusCode.OK);
        });
        var source = new PoisonSwitchSource();
        source.WhenNotMatched(LogEventLevel.Verbose);
        var sink = new WatchSink(CreateClient(handler), source, "test");

        var events = new List<SerilogEvent>
        {
            MakeSerilogEvent(sourceContext: "poison"),   // ConvertEvent throws for this one
            MakeSerilogEvent(sourceContext: "GoodApp"),
        };
        await sink.FlushBatchAsync(events);

        handler.Requests.Count.ShouldBe(1);
        received.ShouldNotBeNull();
        received.Events.Count.ShouldBe(1);
        received.Events[0].Category.ShouldBe("GoodApp");
    }

    // --- Dispose ownership ---

    [Fact]
    public void Dispose_OwnsDependencies_DisposesSwitchSourceAndClient()
    {
        var handler = new TrackingHandler();
        var client = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:11000") };
        var source = new TrackingSwitchSource();
        source.WhenNotMatched(LogEventLevel.Verbose);
        var sink = new WatchSink(client, source, "test", ownsDependencies: true);

        sink.Dispose();

        source.DisposeCalled.ShouldBeTrue();
        source.StopCalled.ShouldBeFalse();
        handler.DisposeCalled.ShouldBeTrue();
    }

    [Fact]
    public void Dispose_DoesNotOwnDependencies_OnlyStopsSwitchSource()
    {
        var handler = new TrackingHandler();
        var client = new HttpClient(handler) { BaseAddress = new Uri("http://localhost:11000") };
        var source = new TrackingSwitchSource();
        source.WhenNotMatched(LogEventLevel.Verbose);
        var sink = new WatchSink(client, source, "test");

        sink.Dispose();

        source.StopCalled.ShouldBeTrue();
        source.DisposeCalled.ShouldBeFalse();
        handler.DisposeCalled.ShouldBeFalse();
    }

    // --- Test doubles ---

    private sealed class PoisonSwitchSource : SwitchSource
    {
        public override Switch Lookup(string category)
        {
            if (string.Equals(category, "poison", StringComparison.Ordinal))
                throw new InvalidOperationException("poison lookup");
            return base.Lookup(category);
        }
    }

    private sealed class TrackingSwitchSource : SwitchSource
    {
        public bool StopCalled { get; private set; }
        public bool DisposeCalled { get; private set; }

        public override void Stop()
        {
            StopCalled = true;
            base.Stop();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                DisposeCalled = true;
            base.Dispose(disposing);
        }
    }

    private sealed class TrackingHandler : HttpMessageHandler
    {
        public bool DisposeCalled { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            => Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));

        protected override void Dispose(bool disposing)
        {
            if (disposing)
                DisposeCalled = true;
            base.Dispose(disposing);
        }
    }
}
