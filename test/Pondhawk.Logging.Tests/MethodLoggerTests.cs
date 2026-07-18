// Copyright (c) Pond Hawk Technologies Inc. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using Microsoft.Extensions.Logging;
using Pondhawk.Logging.Tests.Support;
using Shouldly;
using Xunit;

namespace Pondhawk.Logging.Tests;

public class MethodLoggerTests
{
    [Fact]
    public void EnterMethod_LogsEntering_WithNestingOne()
    {
        var inner = new CollectingLogger();

        using var method = inner.EnterMethod();

        var entry = inner.Entries.ShouldHaveSingleItem();
        entry.Message.ShouldBe("Entering " + nameof(EnterMethod_LogsEntering_WithNestingOne));
        CollectingLogger.Prop(entry, LogPropertyNames.Nesting).ShouldBe(1);
    }

    [Fact]
    public void Dispose_LogsExiting_WithNestingMinusOne()
    {
        var inner = new CollectingLogger();

        var method = inner.EnterMethod();
        method.Dispose();

        var exit = inner.Entries.Last();
        exit.Message.ShouldStartWith("Exiting " + nameof(Dispose_LogsExiting_WithNestingMinusOne));
        CollectingLogger.Prop(exit, LogPropertyNames.Nesting).ShouldBe(-1);
    }

    [Fact]
    public void MethodLogger_DelegatesLog_ToInnerLogger()
    {
        var inner = new CollectingLogger();

        using var method = inner.EnterMethod();
        method.LogInformation("inside {Where}", "body");

        inner.Entries.ShouldContain(e => e.Message == "inside body");
    }

    [Fact]
    public void MethodLogger_IsEnabled_DelegatesToInner()
    {
        var inner = new CollectingLogger(LogLevel.Warning);

        using var method = inner.EnterMethod();

        method.IsEnabled(LogLevel.Debug).ShouldBeFalse();
        method.IsEnabled(LogLevel.Warning).ShouldBeTrue();
    }

    [Fact]
    public void MethodLogger_DoubleDispose_LogsExitOnlyOnce()
    {
        var inner = new CollectingLogger();

        var method = inner.EnterMethod();
        method.Dispose();
        method.Dispose();

        inner.Entries.Count(e => e.Message.StartsWith("Exiting")).ShouldBe(1);
    }
}
