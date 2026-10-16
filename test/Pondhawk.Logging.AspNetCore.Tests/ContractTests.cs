// Copyright (c) Pond Hawk Technologies Inc. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Pondhawk.Logging.Watch;
using Shouldly;
using Xunit;

namespace Pondhawk.Logging.AspNetCore.Tests;

/// <summary>
/// The middlewares do not reference Pondhawk.Logging — an application vendors it or drops it — so they keep their
/// own copy of the probe mark and of how a JSON payload is written, and take the probe key from the Watch provider
/// through a keyed service of built-in types. These check each against the other side.
/// </summary>
public class ContractTests
{
    [Fact]
    public void AProbeTheseBegin_IsSeenByTheApi()
    {
        using (new Activity("probed").Start())
        {
            DiagnosticProbe.IsActive.ShouldBeFalse();
            ProbeContract.Begin().ShouldBeTrue();
            DiagnosticProbe.IsActive.ShouldBeTrue();
        }

        DiagnosticProbe.IsActive.ShouldBeFalse();
    }

    [Fact]
    public void WithNoActivity_NoProbeBegins() =>
        ProbeContract.Begin().ShouldBeFalse();

    [Fact]
    public void TheKeyTheWatchProviderRegisters_IsFound()
    {
        var services = new ServiceCollection();
        services.AddLogging(b => b.AddWatch(new WatchDestination("http://127.0.0.1:9/D"), o => o.PollInterval = TimeSpan.FromMinutes(5)));
        using var provider = services.BuildServiceProvider();

        var keys = ProbeContract.KeySource(new DefaultHttpContext { RequestServices = provider }).ShouldNotBeNull();
        keys().ShouldBeNull();   // nothing polled yet
    }

    [Fact]
    public void WithNoWatchProvider_ThereIsNoKeySource()
    {
        using var provider = new ServiceCollection().BuildServiceProvider();

        ProbeContract.KeySource(new DefaultHttpContext { RequestServices = provider }).ShouldBeNull();
    }

    [Fact]
    public void AContainerWithoutKeyedServices_HasNoKeySource() =>
        ProbeContract.KeySource(new DefaultHttpContext { RequestServices = new PlainServices() }).ShouldBeNull();

    [Fact]
    public void AJsonPayload_IsTheEventLogJsonWrites()
    {
        var ours = new Capture();
        var api = new Capture();

        JsonPayload.Log(ours, LogLevel.Debug, "HTTP GET /orders", """{"Method":"GET"}""");
        api.LogJson(LogLevel.Debug, "HTTP GET /orders", """{"Method":"GET"}""");

        ours.Level.ShouldBe(api.Level);
        ours.Message.ShouldBe(api.Message);
        ours.Properties.ShouldBe(api.Properties);
    }

    [Fact]
    public void AJsonPayload_AtALevelThatIsOff_IsNotWritten()
    {
        var ours = new Capture { Enabled = false };

        JsonPayload.Log(ours, LogLevel.Debug, "title", "{}");

        ours.Message.ShouldBeNull();
    }

    [Fact]
    public void TheMiddlewares_DoNotReferencePondhawkLogging() =>
        typeof(RequestLoggingMiddleware).Assembly.GetReferencedAssemblies()
            .Select(a => a.Name)
            .ShouldNotContain("Pondhawk.Logging");

    private sealed class PlainServices : IServiceProvider
    {
        public object GetService(Type serviceType) => null;
    }

    private sealed class Capture : ILogger
    {
        public bool Enabled { get; init; } = true;

        public LogLevel Level { get; private set; }

        public string Message { get; private set; }

        public List<KeyValuePair<string, object>> Properties { get; private set; }

        public IDisposable BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => Enabled;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception exception, Func<TState, Exception, string> formatter)
        {
            Level = logLevel;
            Message = formatter(state, exception);
            Properties = ((IEnumerable<KeyValuePair<string, object>>)state).ToList();
        }
    }
}
