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
