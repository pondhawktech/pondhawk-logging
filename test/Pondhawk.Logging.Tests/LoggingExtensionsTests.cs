// Copyright (c) Pond Hawk Technologies Inc. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using Microsoft.Extensions.Logging;
using Pondhawk.Logging.Tests.Support;
using Shouldly;
using Xunit;

namespace Pondhawk.Logging.Tests;

public class LoggingExtensionsTests
{
    // ── LogObject ──

    [Fact]
    public void LogObject_EmitsJsonPayload_WithTypeNameAsMessage()
    {
        var logger = new CollectingLogger();

        logger.LogObject(new { Name = "Ada", Age = 36 });

        var e = logger.Entries.ShouldHaveSingleItem();
        e.Level.ShouldBe(LogLevel.Trace);
        CollectingLogger.Prop(e, LogPropertyNames.PayloadType).ShouldBe((int)PayloadType.Json);
        var json = (string)CollectingLogger.Prop(e, LogPropertyNames.PayloadContent);
        json.ShouldContain("Ada");
        json.ShouldContain("36");
    }

    [Fact]
    public void LogObject_WithTitle_UsesTitleAsMessage()
    {
        var logger = new CollectingLogger();

        logger.LogObject("The user", new { Name = "Ada" });

        var e = logger.Entries.ShouldHaveSingleItem();
        e.Message.ShouldBe("The user");
        CollectingLogger.Prop(e, LogPropertyNames.PayloadType).ShouldBe((int)PayloadType.Json);
    }

    [Fact]
    public void LogObject_WhenTraceDisabled_EmitsNothing()
    {
        var logger = new CollectingLogger(LogLevel.Information);

        logger.LogObject(new { Name = "Ada" });
        logger.LogObject("title", new { Name = "Ada" });

        logger.Entries.ShouldBeEmpty();
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
        var logger = new CollectingLogger();

        switch (type)
        {
            case PayloadType.Json: logger.LogJson("t", "{\"a\":1}"); break;
            case PayloadType.Sql: logger.LogSql("t", "select 1"); break;
            case PayloadType.Xml: logger.LogXml("t", "<a/>"); break;
            case PayloadType.Yaml: logger.LogYaml("t", "a: 1"); break;
            default: logger.LogText("t", "hello"); break;
        }

        var e = logger.Entries.ShouldHaveSingleItem();
        e.Message.ShouldBe("t");
        CollectingLogger.Prop(e, LogPropertyNames.PayloadType).ShouldBe((int)type);
    }

    [Fact]
    public void TypedPayload_NullContent_EmitsEmptyString()
    {
        var logger = new CollectingLogger();

        logger.LogJson("t", null);

        var e = logger.Entries.ShouldHaveSingleItem();
        CollectingLogger.Prop(e, LogPropertyNames.PayloadContent).ShouldBe(string.Empty);
    }

    [Fact]
    public void TypedPayload_WhenLevelDisabled_EmitsNothing()
    {
        var logger = new CollectingLogger(LogLevel.Information);

        logger.LogJson("t", "{}");

        logger.Entries.ShouldBeEmpty();
    }

    [Theory]
    [InlineData(PayloadType.Json)]
    [InlineData(PayloadType.Sql)]
    [InlineData(PayloadType.Xml)]
    [InlineData(PayloadType.Yaml)]
    [InlineData(PayloadType.Text)]
    public void TypedPayload_DefaultsToDebug(PayloadType type)
    {
        var logger = new CollectingLogger();

        Emit(logger, type, level: null);

        logger.Entries.ShouldHaveSingleItem().Level.ShouldBe(LogLevel.Debug);
    }

    // ── Typed payloads at a caller-chosen level ──

    [Theory]
    [InlineData(PayloadType.Json)]
    [InlineData(PayloadType.Sql)]
    [InlineData(PayloadType.Xml)]
    [InlineData(PayloadType.Yaml)]
    [InlineData(PayloadType.Text)]
    public void TypedPayload_WithLevel_EmitsAtThatLevelWithContent(PayloadType type)
    {
        var logger = new CollectingLogger();

        Emit(logger, type, LogLevel.Error);

        var e = logger.Entries.ShouldHaveSingleItem();
        e.Level.ShouldBe(LogLevel.Error);
        e.Message.ShouldBe("t");
        CollectingLogger.Prop(e, LogPropertyNames.PayloadType).ShouldBe((int)type);
        ((string)CollectingLogger.Prop(e, LogPropertyNames.PayloadContent)).ShouldNotBeEmpty();
    }

    [Fact]
    public void TypedPayload_WithLevel_SurvivesAProductionLevelThatDropsTrace()
    {
        // The point of the level overloads: the payload explaining a failure rides on the failure's own
        // level, so a host logging at Information still gets it.
        var logger = new CollectingLogger(LogLevel.Information);

        logger.LogJson("dropped", "{}");
        logger.LogJson(LogLevel.Error, "kept", "{\"bad\":true}");

        var e = logger.Entries.ShouldHaveSingleItem();
        e.Message.ShouldBe("kept");
        e.Level.ShouldBe(LogLevel.Error);
    }

    [Fact]
    public void TypedPayload_WithLevel_WhenLevelDisabled_EmitsNothing()
    {
        var logger = new CollectingLogger(LogLevel.Warning);

        logger.LogText(LogLevel.Debug, "t", "hello");

        logger.Entries.ShouldBeEmpty();
    }

    [Fact]
    public void LogObject_WithLevel_EmitsJsonPayloadAtThatLevel()
    {
        var logger = new CollectingLogger();

        logger.LogObject(LogLevel.Error, "the result", new { Successful = false });
        logger.LogObject(LogLevel.Information, new { Name = "Ada" });

        logger.Entries.Count.ShouldBe(2);

        var titled = logger.Entries[0];
        titled.Level.ShouldBe(LogLevel.Error);
        titled.Message.ShouldBe("the result");
        ((string)CollectingLogger.Prop(titled, LogPropertyNames.PayloadContent)).ShouldContain("Successful");

        var untitled = logger.Entries[1];
        untitled.Level.ShouldBe(LogLevel.Information);
        CollectingLogger.Prop(untitled, LogPropertyNames.PayloadType).ShouldBe((int)PayloadType.Json);
    }

    [Fact]
    public void LogObject_WithLevel_WhenLevelDisabled_EmitsNothing()
    {
        var logger = new CollectingLogger(LogLevel.Warning);

        logger.LogObject(LogLevel.Debug, "t", new { Name = "Ada" });
        logger.LogObject(LogLevel.Debug, new { Name = "Ada" });

        logger.Entries.ShouldBeEmpty();
    }

    private static void Emit(CollectingLogger logger, PayloadType type, LogLevel? level)
    {
        switch (type, level)
        {
            case (PayloadType.Json, null): logger.LogJson("t", "{\"a\":1}"); break;
            case (PayloadType.Json, _): logger.LogJson(level.Value, "t", "{\"a\":1}"); break;
            case (PayloadType.Sql, null): logger.LogSql("t", "select 1"); break;
            case (PayloadType.Sql, _): logger.LogSql(level.Value, "t", "select 1"); break;
            case (PayloadType.Xml, null): logger.LogXml("t", "<a/>"); break;
            case (PayloadType.Xml, _): logger.LogXml(level.Value, "t", "<a/>"); break;
            case (PayloadType.Yaml, null): logger.LogYaml("t", "a: 1"); break;
            case (PayloadType.Yaml, _): logger.LogYaml(level.Value, "t", "a: 1"); break;
            case (_, null): logger.LogText("t", "hello"); break;
            default: logger.LogText(level.Value, "t", "hello"); break;
        }
    }

    // ── ErrorWithContext ──

    [Fact]
    public void ErrorWithContext_EmitsErrorCarryingExceptionAndSerializedContext()
    {
        var logger = new CollectingLogger();
        var cause = new InvalidOperationException("boom");

        logger.ErrorWithContext(cause, new { InstanceId = "i-0abc", Arn = "arn:tg" }, "Failed to deregister");

        var e = logger.Entries.ShouldHaveSingleItem();
        e.Level.ShouldBe(LogLevel.Error);
        e.Message.ShouldBe("Failed to deregister");
        e.Exception.ShouldBeSameAs(cause);

        var context = (string)CollectingLogger.Prop(e, LogPropertyNames.ErrorContext);
        context.ShouldContain("i-0abc");
        context.ShouldContain("arn:tg");
    }

    [Fact]
    public void ErrorWithContext_WhenErrorDisabled_EmitsNothing()
    {
        var logger = new CollectingLogger(LogLevel.Critical);

        logger.ErrorWithContext(new InvalidOperationException("boom"), new { Id = 1 }, "failed");

        logger.Entries.ShouldBeEmpty();
    }

    // ── Inspect ──

    [Fact]
    public void Inspect_EmitsNameEqualsValueAtDebug()
    {
        var logger = new CollectingLogger();

        logger.Inspect("discount", 15);

        var e = logger.Entries.ShouldHaveSingleItem();
        e.Level.ShouldBe(LogLevel.Debug);
        e.Message.ShouldBe("discount = 15");
        CollectingLogger.Prop(e, "Name").ShouldBe("discount");
        CollectingLogger.Prop(e, "Value").ShouldBe(15);
    }

    [Fact]
    public void Inspect_NullValue_DoesNotThrow()
    {
        var logger = new CollectingLogger();

        logger.Inspect("thing", null);

        logger.Entries.ShouldHaveSingleItem();
    }

    // ── EnterMethod ──

    [Fact]
    public void EnterMethod_LogsEntryAndExit_WithNesting()
    {
        var logger = new CollectingLogger();

        using (logger.EnterMethod())
        {
            // method body
        }

        logger.Entries.Count.ShouldBe(2);
        var entry = logger.Entries[0];
        var exit = logger.Entries[1];

        entry.Message.ShouldBe("Entering " + nameof(EnterMethod_LogsEntryAndExit_WithNesting));
        CollectingLogger.Prop(entry, LogPropertyNames.Nesting).ShouldBe(1);

        exit.Message.ShouldStartWith("Exiting " + nameof(EnterMethod_LogsEntryAndExit_WithNesting));
        CollectingLogger.Prop(exit, LogPropertyNames.Nesting).ShouldBe(-1);
    }

    [Fact]
    public void EnterMethod_WhenTraceDisabled_LogsNoTrace()
    {
        var logger = new CollectingLogger(LogLevel.Information);

        using (logger.EnterMethod())
        {
        }

        logger.Entries.ShouldBeEmpty();
    }
}
