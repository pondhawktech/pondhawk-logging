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
        e.Level.ShouldBe(LogLevel.Trace);
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

    [Fact]
    public void ErrorWithContext_WithoutException_EmitsErrorCarryingContextAndNoException()
    {
        var logger = new CollectingLogger();

        logger.ErrorWithContext(new { InstanceId = "i-0abc" }, "Nothing was deregistered");

        var e = logger.Entries.ShouldHaveSingleItem();
        e.Level.ShouldBe(LogLevel.Error);
        e.Message.ShouldBe("Nothing was deregistered");
        e.Exception.ShouldBeNull();
        ((string)CollectingLogger.Prop(e, LogPropertyNames.ErrorContext)).ShouldContain("i-0abc");
    }

    [Fact]
    public void ErrorWithContext_ExceptionAndMessageOnly_LogsTheExceptionNotAsContext()
    {
        var logger = new CollectingLogger();
        var cause = new InvalidOperationException("boom");

        // Binds to (Exception, string), not (object, string): the exception is the cause, never serialized.
        logger.ErrorWithContext(cause, "Failed to deregister");

        var e = logger.Entries.ShouldHaveSingleItem();
        e.Level.ShouldBe(LogLevel.Error);
        e.Message.ShouldBe("Failed to deregister");
        e.Exception.ShouldBeSameAs(cause);
        CollectingLogger.Prop(e, LogPropertyNames.ErrorContext).ShouldBeNull();
    }

    [Fact]
    public void ErrorWithContext_MessageIsATitleNotATemplate()
    {
        var logger = new CollectingLogger();

        logger.ErrorWithContext(new { Id = 1 }, "Order {Id} failed");

        logger.Entries.ShouldHaveSingleItem().Message.ShouldBe("Order {Id} failed");
    }

    [Fact]
    public void WarningWithContext_EmitsWarningCarryingExceptionAndSerializedContext()
    {
        var logger = new CollectingLogger();
        var cause = new TimeoutException("slow");

        logger.WarningWithContext(cause, new { TargetGroup = "arn:tg" }, "Drain timed out");

        var e = logger.Entries.ShouldHaveSingleItem();
        e.Level.ShouldBe(LogLevel.Warning);
        e.Message.ShouldBe("Drain timed out");
        e.Exception.ShouldBeSameAs(cause);
        ((string)CollectingLogger.Prop(e, LogPropertyNames.ErrorContext)).ShouldContain("arn:tg");
    }

    [Fact]
    public void WarningWithContext_WithoutException_EmitsWarningCarryingContextAndNoException()
    {
        var logger = new CollectingLogger();

        logger.WarningWithContext(new { InstanceId = "i-0abc" }, "Skipping the drain wait");

        var e = logger.Entries.ShouldHaveSingleItem();
        e.Level.ShouldBe(LogLevel.Warning);
        e.Message.ShouldBe("Skipping the drain wait");
        e.Exception.ShouldBeNull();
        ((string)CollectingLogger.Prop(e, LogPropertyNames.ErrorContext)).ShouldContain("i-0abc");
    }

    [Fact]
    public void WarningWithContext_ExceptionAndMessageOnly_LogsTheExceptionNotAsContext()
    {
        var logger = new CollectingLogger();
        var cause = new TimeoutException("slow");

        logger.WarningWithContext(cause, "Drain timed out");

        var e = logger.Entries.ShouldHaveSingleItem();
        e.Level.ShouldBe(LogLevel.Warning);
        e.Exception.ShouldBeSameAs(cause);
        CollectingLogger.Prop(e, LogPropertyNames.ErrorContext).ShouldBeNull();
    }

    [Theory]
    [InlineData(LogLevel.Trace)]
    [InlineData(LogLevel.Debug)]
    [InlineData(LogLevel.Information)]
    public void LogWithContext_EmitsAtTheGivenLevel_WithOrWithoutAnException(LogLevel level)
    {
        var logger = new CollectingLogger();
        var cause = new InvalidOperationException("boom");

        logger.LogWithContext(level, new { InstanceId = "i-0abc" }, "Placing");
        logger.LogWithContext(level, cause, new { InstanceId = "i-0def" }, "Placing failed");
        logger.LogWithContext(level, cause, "Retrying");

        logger.Entries.Count.ShouldBe(3);
        logger.Entries.ShouldAllBe(e => e.Level == level);

        logger.Entries[0].Exception.ShouldBeNull();
        ((string)CollectingLogger.Prop(logger.Entries[0], LogPropertyNames.ErrorContext)).ShouldContain("i-0abc");

        logger.Entries[1].Exception.ShouldBeSameAs(cause);
        ((string)CollectingLogger.Prop(logger.Entries[1], LogPropertyNames.ErrorContext)).ShouldContain("i-0def");

        // (level, Exception, string) binds to the exception overload: the cause, never serialized as context.
        logger.Entries[2].Exception.ShouldBeSameAs(cause);
        CollectingLogger.Prop(logger.Entries[2], LogPropertyNames.ErrorContext).ShouldBeNull();
    }

    [Fact]
    public void WithContext_WhenLevelDisabled_SerializesNothing()
    {
        var errorsOnly = new CollectingLogger(LogLevel.Error);
        var none = new CollectingLogger(LogLevel.Critical);
        var context = new CountsReads();

        errorsOnly.WarningWithContext(context, "dropped");
        errorsOnly.WarningWithContext(new InvalidOperationException(), context, "dropped");
        errorsOnly.WarningWithContext(new InvalidOperationException(), "dropped");
        none.ErrorWithContext(context, "dropped");
        none.ErrorWithContext(new InvalidOperationException(), "dropped");
        errorsOnly.LogWithContext(LogLevel.Information, context, "dropped");
        errorsOnly.LogWithContext(LogLevel.Debug, new InvalidOperationException(), context, "dropped");

        errorsOnly.Entries.ShouldBeEmpty();
        none.Entries.ShouldBeEmpty();
        context.Reads.ShouldBe(0);

        errorsOnly.ErrorWithContext(context, "kept");   // and the counter does see a serialization
        context.Reads.ShouldBe(1);
    }

    private sealed class CountsReads
    {
        public int Reads { get; private set; }

        public string Value
        {
            get
            {
                Reads++;
                return "read";
            }
        }
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
        entry.Level.ShouldBe(LogLevel.Debug);
        CollectingLogger.Prop(entry, LogPropertyNames.Nesting).ShouldBe(1);

        exit.Message.ShouldStartWith("Exiting " + nameof(EnterMethod_LogsEntryAndExit_WithNesting));
        exit.Level.ShouldBe(LogLevel.Debug);
        CollectingLogger.Prop(exit, LogPropertyNames.Nesting).ShouldBe(-1);
    }

    [Fact]
    public void EnterMethod_WhenDebugEnabledAndTraceNot_LogsEntryAndExit()
    {
        var logger = new CollectingLogger(LogLevel.Debug);

        using (logger.EnterMethod())
        {
        }

        logger.Entries.Count.ShouldBe(2);
    }

    [Fact]
    public void EnterMethod_WhenDebugDisabled_LogsNothing()
    {
        var logger = new CollectingLogger(LogLevel.Information);

        using (logger.EnterMethod())
        {
        }

        logger.Entries.ShouldBeEmpty();
    }
}
