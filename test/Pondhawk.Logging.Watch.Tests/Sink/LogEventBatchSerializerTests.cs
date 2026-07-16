using Shouldly;
using Xunit;

namespace Pondhawk.Logging.Watch.Tests;

public class LogEventBatchSerializerTests
{

    private static LogEventBatch CreateSampleBatch()
    {
        return new LogEventBatch
        {
            Uid = "01HTEST000000000000000TEST",
            Domain = "test-domain",
            Events =
            [
                new LogEvent
                {
                    Category = "MyApp.Services",
                    CorrelationId = "corr-123",
                    Title = "Something happened",
                    Tenant = "tenant-1",
                    Subject = "user-42",
                    Tag = "infra",
                    Level = 3,
                    Color = 0xFF0000,
                    Nesting = 1,
                    Occurred = new DateTime(2025, 6, 15, 12, 0, 0, DateTimeKind.Utc),
                    Type = 2,
                    Payload = "{\"key\":\"value\"}",
                    ErrorType = "InvalidOperationException"
                }
            ]
        };
    }

    // --- Binary/Stream round-trip ---

    [Fact]
    public async Task ToStream_FromStream_RoundTrip_PreservesAllFields()
    {
        var original = CreateSampleBatch();

        var stream = await LogEventBatchSerializer.ToStream(original);
        var restored = await LogEventBatchSerializer.FromStream(stream);

        restored.ShouldNotBeNull();
        restored.Uid.ShouldBe(original.Uid);
        restored.Domain.ShouldBe("test-domain");
        restored.Events.Count.ShouldBe(1);

        var ev = restored.Events[0];
        ev.Category.ShouldBe("MyApp.Services");
        ev.CorrelationId.ShouldBe("corr-123");
        ev.Title.ShouldBe("Something happened");
        ev.Tenant.ShouldBe("tenant-1");
        ev.Subject.ShouldBe("user-42");
        ev.Tag.ShouldBe("infra");
        ev.Level.ShouldBe(3);
        ev.Color.ShouldBe(0xFF0000);
        ev.Nesting.ShouldBe(1);
        ev.Occurred.ShouldBe(new DateTime(2025, 6, 15, 12, 0, 0, DateTimeKind.Utc));
        ev.Type.ShouldBe(2);
        ev.Payload.ShouldBe("{\"key\":\"value\"}");
        ev.ErrorType.ShouldBe("InvalidOperationException");
    }

    [Fact]
    public async Task ToStream_ReturnsNonEmptyStream()
    {
        var batch = CreateSampleBatch();

        var stream = await LogEventBatchSerializer.ToStream(batch);

        stream.Length.ShouldBeGreaterThan(0);
        stream.Position.ShouldBe(0);
    }

    [Fact]
    public async Task ToStream_EmptyBatch_RoundTrips()
    {
        var original = new LogEventBatch { Uid = "empty-uid", Domain = "d" };

        var stream = await LogEventBatchSerializer.ToStream(original);
        var restored = await LogEventBatchSerializer.FromStream(stream);

        restored.ShouldNotBeNull();
        restored.Uid.ShouldBe("empty-uid");
        restored.Domain.ShouldBe("d");
        restored.Events.ShouldBeEmpty();
    }

    [Fact]
    public async Task ToStream_MultipleEvents_RoundTrips()
    {
        var batch = new LogEventBatch
        {
            Domain = "multi",
            Events =
            [
                new LogEvent { Title = "first" },
                new LogEvent { Title = "second" },
                new LogEvent { Title = "third" }
            ]
        };

        var stream = await LogEventBatchSerializer.ToStream(batch);
        var restored = await LogEventBatchSerializer.FromStream(stream);

        restored.ShouldNotBeNull();
        restored.Events.Count.ShouldBe(3);
        restored.Events[0].Title.ShouldBe("first");
        restored.Events[1].Title.ShouldBe("second");
        restored.Events[2].Title.ShouldBe("third");
    }

    // --- ToStream with target stream ---

    [Fact]
    public async Task ToStream_WithTargetStream_WritesData()
    {
        var batch = CreateSampleBatch();

        using var target = new MemoryStream();
        await LogEventBatchSerializer.ToStream(batch, target);

        target.Length.ShouldBeGreaterThan(0);
    }

    [Fact]
    public async Task ToStream_WithTargetStream_RoundTrips()
    {
        var batch = CreateSampleBatch();

        using var target = new MemoryStream();
        await LogEventBatchSerializer.ToStream(batch, target);
        target.Position = 0;

        var restored = await LogEventBatchSerializer.FromStream(target);

        restored.ShouldNotBeNull();
        restored.Domain.ShouldBe("test-domain");
        restored.Events.Count.ShouldBe(1);
        restored.Events[0].Title.ShouldBe("Something happened");
    }

    // --- JSON round-trip ---

    [Fact]
    public void ToJson_ReturnsValidJsonString()
    {
        var batch = CreateSampleBatch();

        var json = LogEventBatchSerializer.ToJson(batch);

        json.ShouldNotBeNullOrWhiteSpace();
        json.ShouldContain("test-domain");
        json.ShouldContain("Something happened");
    }

    [Fact]
    public void ToJson_FromJson_RoundTrip_PreservesAllFields()
    {
        var original = CreateSampleBatch();

        var json = LogEventBatchSerializer.ToJson(original);
        var restored = LogEventBatchSerializer.FromJson(json);

        restored.ShouldNotBeNull();
        restored.Uid.ShouldBe(original.Uid);
        restored.Domain.ShouldBe("test-domain");
        restored.Events.Count.ShouldBe(1);

        var ev = restored.Events[0];
        ev.Category.ShouldBe("MyApp.Services");
        ev.CorrelationId.ShouldBe("corr-123");
        ev.Title.ShouldBe("Something happened");
        ev.Tenant.ShouldBe("tenant-1");
        ev.Subject.ShouldBe("user-42");
        ev.Tag.ShouldBe("infra");
        ev.Level.ShouldBe(3);
        ev.Color.ShouldBe(0xFF0000);
        ev.Nesting.ShouldBe(1);
        ev.Type.ShouldBe(2);
        ev.Payload.ShouldBe("{\"key\":\"value\"}");
        ev.ErrorType.ShouldBe("InvalidOperationException");
    }

    [Fact]
    public void ToJson_EmptyBatch_RoundTrips()
    {
        var original = new LogEventBatch { Uid = "empty", Domain = "d" };

        var json = LogEventBatchSerializer.ToJson(original);
        var restored = LogEventBatchSerializer.FromJson(json);

        restored.ShouldNotBeNull();
        restored.Uid.ShouldBe("empty");
        restored.Events.ShouldBeEmpty();
    }

    [Fact]
    public void ToJson_NullPayload_RoundTrips()
    {
        var batch = new LogEventBatch
        {
            Domain = "d",
            Events = [new LogEvent { Title = "no payload", Payload = null, ErrorType = null }]
        };

        var json = LogEventBatchSerializer.ToJson(batch);
        var restored = LogEventBatchSerializer.FromJson(json);

        restored.ShouldNotBeNull();
        restored.Events[0].Payload.ShouldBeNull();
        restored.Events[0].ErrorType.ShouldBeNull();
    }

    [Fact]
    public void ToJson_MultipleEvents_RoundTrips()
    {
        var batch = new LogEventBatch
        {
            Domain = "multi",
            Events =
            [
                new LogEvent { Title = "a", Level = 1 },
                new LogEvent { Title = "b", Level = 2 },
                new LogEvent { Title = "c", Level = 3 }
            ]
        };

        var json = LogEventBatchSerializer.ToJson(batch);
        var restored = LogEventBatchSerializer.FromJson(json);

        restored.ShouldNotBeNull();
        restored.Events.Count.ShouldBe(3);
        restored.Events[0].Title.ShouldBe("a");
        restored.Events[1].Title.ShouldBe("b");
        restored.Events[2].Title.ShouldBe("c");
    }

    // --- Non-serializable fields are excluded ---

    [Fact]
    public async Task Binary_IgnoresNonSerializableFields()
    {
        var batch = new LogEventBatch
        {
            Domain = "d",
            Events =
            [
                new LogEvent
                {
                    Title = "with extras",
                    Object = new { Name = "test" },
                    Error = new InvalidOperationException("boom"),
                    ErrorContext = new object()
                }
            ]
        };

        var stream = await LogEventBatchSerializer.ToStream(batch);
        var restored = await LogEventBatchSerializer.FromStream(stream);

        restored.ShouldNotBeNull();
        restored.Events[0].Title.ShouldBe("with extras");
        restored.Events[0].Object.ShouldBeNull();
        restored.Events[0].Error.ShouldBeNull();
        restored.Events[0].ErrorContext.ShouldBeNull();
    }

    [Fact]
    public void Json_IgnoresNonSerializableFields()
    {
        var batch = new LogEventBatch
        {
            Domain = "d",
            Events =
            [
                new LogEvent
                {
                    Title = "with extras",
                    Object = new { Name = "test" },
                    Error = new InvalidOperationException("boom"),
                    ErrorContext = new object()
                }
            ]
        };

        var json = LogEventBatchSerializer.ToJson(batch);

        // Object, Error, ErrorContext are [JsonIgnore] — they should not appear as JSON keys
        json.ShouldNotContain("\"Object\"");
        json.ShouldNotContain("\"ErrorContext\"");
        // "Error" key should not appear, but "ErrorType" is a valid serialized field
        json.ShouldNotContain("\"Error\":");
    }

}
