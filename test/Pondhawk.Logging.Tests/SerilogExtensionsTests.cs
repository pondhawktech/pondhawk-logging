using Pondhawk.Logging.Tests.Support;
using Serilog.Events;
using Shouldly;
using Xunit;

namespace Pondhawk.Logging.Tests;

public class SerilogExtensionsTests
{
    // ── LogObject ──

    [Fact]
    public void LogObject_EmitsJsonPayload_WithTypeNameAsMessage()
    {
        var (logger, sink) = CollectingSink.Build();

        logger.LogObject(new { Name = "Ada", Age = 36 });

        var e = sink.Events.ShouldHaveSingleItem();
        e.Level.ShouldBe(LogEventLevel.Verbose);
        CollectingSink.Prop(e, LogPropertyNames.PayloadType).ShouldBe((int)PayloadType.Json);
        var json = (string)CollectingSink.Prop(e, LogPropertyNames.PayloadContent)!;
        json.ShouldContain("Ada");
        json.ShouldContain("36");
    }

    [Fact]
    public void LogObject_WithTitle_UsesTitleAsMessage()
    {
        var (logger, sink) = CollectingSink.Build();

        logger.LogObject("The user", new { Name = "Ada" });

        var e = sink.Events.ShouldHaveSingleItem();
        CollectingSink.Text(e).ShouldBe("The user");
        CollectingSink.Prop(e, LogPropertyNames.PayloadType).ShouldBe((int)PayloadType.Json);
    }

    [Fact]
    public void LogObject_WhenVerboseDisabled_EmitsNothing()
    {
        var (logger, sink) = CollectingSink.Build(LogEventLevel.Information);

        logger.LogObject(new { Name = "Ada" });
        logger.LogObject("title", new { Name = "Ada" });

        sink.Events.ShouldBeEmpty();
    }

    // ── Typed payloads ──

    [Theory]
    [InlineData(PayloadType.Json)]
    [InlineData(PayloadType.Sql)]
    [InlineData(PayloadType.Xml)]
    [InlineData(PayloadType.Yaml)]
    [InlineData(PayloadType.Text)]
    public void TypedPayload_EmitsContentWithMatchingPayloadType(PayloadType type)
    {
        var (logger, sink) = CollectingSink.Build();

        switch (type)
        {
            case PayloadType.Json: logger.LogJson("t", "{\"a\":1}"); break;
            case PayloadType.Sql: logger.LogSql("t", "select 1"); break;
            case PayloadType.Xml: logger.LogXml("t", "<a/>"); break;
            case PayloadType.Yaml: logger.LogYaml("t", "a: 1"); break;
            default: logger.LogText("t", "hello"); break;
        }

        var e = sink.Events.ShouldHaveSingleItem();
        CollectingSink.Text(e).ShouldBe("t");
        CollectingSink.Prop(e, LogPropertyNames.PayloadType).ShouldBe((int)type);
    }

    [Fact]
    public void TypedPayload_NullContent_EmitsEmptyString()
    {
        var (logger, sink) = CollectingSink.Build();

        logger.LogJson("t", null);

        var e = sink.Events.ShouldHaveSingleItem();
        CollectingSink.Prop(e, LogPropertyNames.PayloadContent).ShouldBe(string.Empty);
    }

    [Fact]
    public void TypedPayload_WhenLevelDisabled_EmitsNothing()
    {
        var (logger, sink) = CollectingSink.Build(LogEventLevel.Information);

        logger.LogJson("t", "{}");

        sink.Events.ShouldBeEmpty();
    }

    // ── Inspect ──

    [Fact]
    public void Inspect_EmitsNameEqualsValueAtDebug()
    {
        var (logger, sink) = CollectingSink.Build();

        logger.Inspect("discount", 15);

        var e = sink.Events.ShouldHaveSingleItem();
        e.Level.ShouldBe(LogEventLevel.Debug);
        CollectingSink.Text(e).ShouldBe("{Name} = {Value}");
        CollectingSink.Prop(e, "Name").ShouldBe("discount");
        CollectingSink.Prop(e, "Value").ShouldBe(15);
    }

    [Fact]
    public void Inspect_NullValue_DoesNotThrow()
    {
        var (logger, sink) = CollectingSink.Build();

        logger.Inspect("thing", null);

        sink.Events.ShouldHaveSingleItem();
    }

    // ── EnterMethod ──

    [Fact]
    public void EnterMethod_LogsEntryAndExit_WithNesting()
    {
        var (logger, sink) = CollectingSink.Build();

        using (logger.EnterMethod())
        {
            // method body
        }

        sink.Events.Count.ShouldBe(2);
        var entry = sink.Events[0];
        var exit = sink.Events[1];

        CollectingSink.Text(entry).ShouldBe("Entering {Method}");
        CollectingSink.Prop(entry, "Method").ShouldBe(nameof(EnterMethod_LogsEntryAndExit_WithNesting));
        CollectingSink.Prop(entry, LogPropertyNames.Nesting).ShouldBe(1);

        CollectingSink.Text(exit).ShouldBe("Exiting {Method} ({Elapsed:F2}ms)");
        CollectingSink.Prop(exit, LogPropertyNames.Nesting).ShouldBe(-1);
    }

    [Fact]
    public void EnterMethod_WhenVerboseDisabled_LogsNoTrace()
    {
        var (logger, sink) = CollectingSink.Build(LogEventLevel.Information);

        using (logger.EnterMethod())
        {
        }

        sink.Events.ShouldBeEmpty();
    }
}
