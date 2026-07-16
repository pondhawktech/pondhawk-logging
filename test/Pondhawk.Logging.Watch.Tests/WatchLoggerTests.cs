// Copyright (c) Pond Hawk Technologies Inc. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using Pondhawk.Logging;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using Shouldly;
using Xunit;
using SerilogEvent = Serilog.Events.LogEvent;

namespace Pondhawk.Logging.Watch.Tests;

public class WatchLoggerTests
{
    private sealed class Capture : ILogEventSink
    {
        public List<SerilogEvent> Events { get; } = new();
        public void Emit(SerilogEvent logEvent) => Events.Add(logEvent);
    }

    private static ILogger RootTo(Capture sink) =>
        new LoggerConfiguration().MinimumLevel.Verbose().WriteTo.Sink(sink).CreateLogger();

    private static SwitchSource Switches(LogEventLevel defaultLevel)
    {
        var source = new SwitchSource();
        source.WhenNotMatched(defaultLevel);
        return source;
    }

    [Fact]
    public void IsEnabled_ReflectsSwitchLevel()
    {
        var log = new WatchLogger(RootTo(new Capture()), "MyApp.Thing", Switches(LogEventLevel.Warning));

        log.IsEnabled(LogEventLevel.Verbose).ShouldBeFalse();
        log.IsEnabled(LogEventLevel.Warning).ShouldBeTrue();
        log.IsEnabled(LogEventLevel.Error).ShouldBeTrue();
    }

    [Fact]
    public void IsEnabled_BlankCategory_DelegatesToInner()
    {
        // Inner minimum is Verbose, so a blank category (which cannot be looked up) is fully enabled.
        var log = new WatchLogger(RootTo(new Capture()), "", Switches(LogEventLevel.Warning));

        log.IsEnabled(LogEventLevel.Verbose).ShouldBeTrue();
    }

    [Fact]
    public void LogObject_SkipsSerialization_WhenSwitchDropsCategory()
    {
        var capture = new Capture();
        var log = new WatchLogger(RootTo(capture), "MyApp.Thing", Switches(LogEventLevel.Warning));

        log.LogObject(new { Name = "Ada" }); // Verbose < Warning -> guarded out, never serialized/emitted

        capture.Events.ShouldBeEmpty();
    }

    [Fact]
    public void LogObject_Emits_WhenSwitchAllowsCategory()
    {
        var capture = new Capture();
        var log = new WatchLogger(RootTo(capture), "MyApp.Thing", Switches(LogEventLevel.Verbose));

        log.LogObject(new { Name = "Ada" });

        capture.Events.ShouldHaveSingleItem();
    }

    [Fact]
    public void ForContext_PreservesSwitchAwareness()
    {
        var log = new WatchLogger(RootTo(new Capture()), "MyApp.Thing", Switches(LogEventLevel.Warning));

        var scoped = log.ForContext("Tenant", "acme");

        scoped.IsEnabled(LogEventLevel.Verbose).ShouldBeFalse();
    }
}
