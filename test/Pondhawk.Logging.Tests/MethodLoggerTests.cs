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
    public void Dispose_WhenDebugTurnedOffAfterEntry_StillLogsExit()
    {
        var inner = new SwitchableLogger { Enabled = true };

        var method = inner.EnterMethod();
        inner.Enabled = false;
        method.Dispose();

        inner.Logged.ShouldBe([(LogLevel.Debug, 1), (LogLevel.Debug, -1)]);
    }

    [Fact]
    public void Dispose_WhenEntryWasNotLogged_LogsNoExit()
    {
        var inner = new SwitchableLogger { Enabled = false };

        var method = inner.EnterMethod();
        inner.Enabled = true;
        method.Dispose();

        inner.Logged.ShouldBeEmpty();
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

    // Records every call, whatever IsEnabled says, so a test sees what MethodLogger asks for.
    private sealed class SwitchableLogger : ILogger
    {
        public bool Enabled { get; set; }

        public List<(LogLevel Level, object Nesting)> Logged { get; } = [];

        public bool IsEnabled(LogLevel logLevel) => Enabled;

        public IDisposable BeginScope<TState>(TState state) where TState : notnull => null;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception exception, Func<TState, Exception, string> formatter)
        {
            var properties = state as IReadOnlyList<KeyValuePair<string, object>> ?? [];
            Logged.Add((logLevel, properties.FirstOrDefault(p => p.Key == LogPropertyNames.Nesting).Value));
        }
    }
}
