// Copyright (c) Pond Hawk Technologies Inc. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using Pondhawk.Logging.Tests.Support;
using Serilog;
using Serilog.Events;
using Shouldly;
using Xunit;

namespace Pondhawk.Logging.Tests;

public class MethodLoggerTests
{
    [Fact]
    public void MethodLogger_DelegatesLogging_ToInnerLogger()
    {
        var (logger, sink) = CollectingSink.Build();

        using var method = logger.EnterMethod();
        // Level methods are ILogger default-interface methods, callable via the interface type.
        ((Serilog.ILogger)method).Information("inside {Where}", "body");

        // Entering + the delegated Information write (Exiting arrives on dispose).
        sink.Events.ShouldContain(e => CollectingSink.Text(e) == "inside {Where}");
        var written = sink.Events.First(e => CollectingSink.Text(e) == "inside {Where}");
        written.Level.ShouldBe(LogEventLevel.Information);
        CollectingSink.Prop(written, "Where").ShouldBe("body");
    }

    [Fact]
    public void MethodLogger_ForContext_ReturnsLogger_ThatWritesToInner()
    {
        var (logger, sink) = CollectingSink.Build();

        using var method = logger.EnterMethod();
        method.ForContext("Tenant", "acme").Warning("scoped");

        var scoped = sink.Events.First(e => CollectingSink.Text(e) == "scoped");
        CollectingSink.Prop(scoped, "Tenant").ShouldBe("acme");
    }

    [Fact]
    public void MethodLogger_IsEnabled_DelegatesToInner()
    {
        var (logger, _) = CollectingSink.Build(LogEventLevel.Warning);

        using var method = logger.EnterMethod();

        method.IsEnabled(LogEventLevel.Debug).ShouldBeFalse();
        method.IsEnabled(LogEventLevel.Warning).ShouldBeTrue();
    }

    [Fact]
    public void MethodLogger_ForwardsAllWriteOverloads_ToInner()
    {
        var (logger, sink) = CollectingSink.Build();
        var ex = new InvalidOperationException("boom");

        using (var method = logger.EnterMethod())
        {
            var il = (Serilog.ILogger)method;
            il.Write(LogEventLevel.Information, "w0");
            il.Write(LogEventLevel.Information, "w1 {A}", 1);
            il.Write(LogEventLevel.Information, "w2 {A} {B}", 1, 2);
            il.Write(LogEventLevel.Information, "w3 {A} {B} {C}", 1, 2, 3);
            il.Write(LogEventLevel.Information, "wN {A} {B} {C} {D}", [1, 2, 3, 4]);
            il.Write(LogEventLevel.Error, ex, "e0");
            il.Write(LogEventLevel.Error, ex, "e1 {A}", 1);
            il.Write(LogEventLevel.Error, ex, "e2 {A} {B}", 1, 2);
            il.Write(LogEventLevel.Error, ex, "e3 {A} {B} {C}", 1, 2, 3);
            il.Write(LogEventLevel.Error, ex, "eN {A} {B} {C} {D}", [1, 2, 3, 4]);
        }

        // 10 forwarded writes reach the inner sink. The Entering/Exiting trace lines start with a
        // capital 'E', so lowercase 'w'/'e' prefixes match only the forwarded messages.
        var forwarded = sink.Events.Count(e =>
            CollectingSink.Text(e)!.StartsWith('w') || CollectingSink.Text(e)!.StartsWith('e'));
        forwarded.ShouldBe(10);
    }

    [Fact]
    public void MethodLogger_ForwardsForContextAndBinding_ToInner()
    {
        var (logger, sink) = CollectingSink.Build();

        using var method = logger.EnterMethod();

        method.ForContext<MethodLoggerTests>().Information("generic-source");
        method.ForContext(typeof(string)).Information("type-source");

        method.BindProperty("K", "v", destructureObjects: false, out var prop).ShouldBeTrue();
        prop!.Name.ShouldBe("K");
        method.BindMessageTemplate("hello {X}", [1], out var template, out var bound).ShouldBeTrue();
        template!.Text.ShouldBe("hello {X}");
        bound.ShouldNotBeNull();

        sink.Events.ShouldContain(e => CollectingSink.Text(e) == "generic-source");
        sink.Events.ShouldContain(e => CollectingSink.Text(e) == "type-source");
    }

    [Fact]
    public void MethodLogger_DoubleDispose_LogsExitOnlyOnce()
    {
        var (logger, sink) = CollectingSink.Build();

        var method = logger.EnterMethod();
        method.Dispose();
        method.Dispose();

        sink.Events.Count(e => CollectingSink.Text(e) == "Exiting {Method} ({Elapsed:F2}ms)")
            .ShouldBe(1);
    }
}
