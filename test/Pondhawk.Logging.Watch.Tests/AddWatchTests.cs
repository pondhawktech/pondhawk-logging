// Copyright (c) Pond Hawk Technologies Inc. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System.Diagnostics;
using System.Drawing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Pondhawk.Logging.Watch.Tests.Http;
using Shouldly;
using Xunit;

namespace Pondhawk.Logging.Watch.Tests;

/// <summary>
/// Tests the <c>AddWatch</c> wiring — specifically that the registered switch filter makes the switch
/// table the level gate at <see cref="ILogger.IsEnabled"/> (driven via the internal overload with a
/// controlled switch source).
/// </summary>
public class AddWatchTests
{
    private static HttpClient CreateClient(MockHttpHandler handler)
        => new(handler) { BaseAddress = new Uri("http://localhost/") };

    private static ILoggerFactory BuildFactory(SwitchSource switches)
    {
        var options = new WatchOptions();
        var destination = new WatchDestination("http://localhost/D");
        return LoggerFactory.Create(b =>
            b.AddWatch(CreateClient(new MockHttpHandler()), switches, destination, options, ownsDependencies: false));
    }

    [Fact]
    public void SwitchFilter_GatesIsEnabled_ByCategory()
    {
        var switches = new SwitchSource();
        switches.WhenMatched("Chatty", "", LogLevel.Trace, Color.White);   // Chatty.* → verbose
        switches.WhenNotMatched(LogLevel.Warning);                          // everything else → Warning

        var factory = BuildFactory(switches);

        factory.CreateLogger("Chatty.Component").IsEnabled(LogLevel.Debug).ShouldBeTrue();
        factory.CreateLogger("Other.Component").IsEnabled(LogLevel.Debug).ShouldBeFalse();
        factory.CreateLogger("Other.Component").IsEnabled(LogLevel.Warning).ShouldBeTrue();
    }

    [Fact]
    public void SwitchFilter_AtNone_DisablesEveryLevel()
    {
        var switches = new SwitchSource();
        switches.WhenMatched("Silenced", "", LogLevel.None, Color.White);
        switches.WhenNotMatched(LogLevel.Warning);

        var factory = BuildFactory(switches);

        factory.CreateLogger("Silenced.Component").IsEnabled(LogLevel.Critical).ShouldBeFalse();
        factory.CreateLogger("Other.Component").IsEnabled(LogLevel.Critical).ShouldBeTrue();
    }

    [Fact]
    public void SwitchFilter_PassesEveryLevel_ForAProbedRequestOnly()
    {
        var switches = new SwitchSource();
        switches.WhenNotMatched(LogLevel.None);

        var factory = BuildFactory(switches);
        var logger = factory.CreateLogger("Orders.Service");

        logger.IsEnabled(LogLevel.Trace).ShouldBeFalse();

        using (new Activity("probed request").Start())
        {
            DiagnosticProbe.Begin();

            logger.IsEnabled(LogLevel.Trace).ShouldBeTrue();
            logger.IsEnabled(LogLevel.None).ShouldBeFalse();
        }

        using (new Activity("next request").Start())
            logger.IsEnabled(LogLevel.Trace).ShouldBeFalse();
    }

    [Fact]
    public void AddWatch_RegistersTheProbeKeySource()
    {
        var destination = new WatchDestination("http://127.0.0.1:9/D");

        var services = new ServiceCollection();
        services.AddLogging(b => b.AddWatch(destination, o => o.PollInterval = TimeSpan.FromMinutes(5)));

        using var provider = services.BuildServiceProvider();

        // Under the key and in the shape the ASP.NET Core diagnostics resolve, from built-in types only.
        provider.GetKeyedService<Func<(string Domain, ReadOnlyMemory<byte> PublicKey)?>>("Pondhawk.DiagnosticProbeKey")
            .ShouldNotBeNull()
            .Invoke().ShouldBeNull();   // no key until Watch sends one
    }

    [Fact]
    public void AddWatch_WithADestination_RegistersItForResolution()
    {
        // Port 9 (discard) refuses immediately, so the switch source's initial fetch fails fast rather
        // than waiting on a server this test has no interest in.
        var destination = new WatchDestination("http://127.0.0.1:9/D");

        var services = new ServiceCollection();
        services.AddLogging(b => b.AddWatch(destination, o => o.PollInterval = TimeSpan.FromMinutes(5)));

        using var provider = services.BuildServiceProvider();

        provider.GetRequiredService<WatchDestination>().ShouldBeSameAs(destination);
    }

    [Fact]
    public async Task AddWatch_PassesTheDefaultTag_ToTheProcessor()
    {
        var delivered = new List<LogEvent>();
        var handler = new MockHttpHandler();
        handler.SetHandler(async (req, ct) =>
        {
            var batch = await LogEventBatchSerializer.FromStream(await req.Content.ReadAsStreamAsync(ct));
            if (batch is not null)
            {
                lock (delivered)
                    delivered.AddRange(batch.Events);
            }

            return new HttpResponseMessage(System.Net.HttpStatusCode.OK);
        });

        var switches = new SwitchSource();
        switches.WhenNotMatched(LogLevel.Information);
        var options = new WatchOptions { FlushInterval = TimeSpan.FromMilliseconds(20), DefaultTag = () => "blue" };
        var destination = new WatchDestination("http://localhost/D");

        using (var factory = LoggerFactory.Create(b =>
            b.AddWatch(CreateClient(handler), switches, destination, options, ownsDependencies: false)))
        {
            factory.CreateLogger("Svc.Thing").LogWarning("warn");
        }

        lock (delivered)
        {
            delivered.ShouldHaveSingleItem().Tag.ShouldBe("blue");
        }
    }

    [Fact]
    public void SwitchFilter_ReactsToLiveSwitchChange_WithoutRebuild()
    {
        var switches = new SwitchSource();
        switches.WhenNotMatched(LogLevel.Warning);

        var factory = BuildFactory(switches);
        var logger = factory.CreateLogger("Svc.Thing");

        logger.IsEnabled(LogLevel.Debug).ShouldBeFalse();   // default Warning drops Debug

        switches.WhenMatched("Svc", "", LogLevel.Debug, Color.White);   // raise Svc live

        logger.IsEnabled(LogLevel.Debug).ShouldBeTrue();    // takes effect with no factory rebuild
    }

    [Fact]
    public void SwitchFilter_PassesDebug_ToWatchOnly_ForAUnitOfWorkMarkedForRetroLogging()
    {
        var switches = new SwitchSource();
        switches.WhenNotMatched(LogLevel.Warning);
        var other = new LevelCapture();

        using var factory = LoggerFactory.Create(b =>
        {
            b.AddWatch(CreateClient(new MockHttpHandler()), switches, new WatchDestination("http://localhost/D"), new WatchOptions(), ownsDependencies: false);
            b.AddProvider(other);
        });
        var logger = factory.CreateLogger("Orders.Service");

        logger.IsEnabled(LogLevel.Debug).ShouldBeFalse();

        using (new Activity("marked request").Start())
        {
            RetroLogging.Begin();

            logger.IsEnabled(LogLevel.Debug).ShouldBeTrue();
            logger.IsEnabled(LogLevel.Trace).ShouldBeFalse();

            logger.LogDebug("kept by Watch only");
        }

        other.Levels.ShouldBeEmpty();

        using (new Activity("next request").Start())
            logger.IsEnabled(LogLevel.Debug).ShouldBeFalse();
    }

    [Fact]
    public async Task AddWatch_SendsTheRetroTrail_OnlyWhenTheUnitOfWorkLogsAnError()
    {
        var delivered = new List<LogEvent>();
        var handler = new MockHttpHandler();
        handler.SetHandler(async (req, ct) =>
        {
            var batch = await LogEventBatchSerializer.FromStream(await req.Content.ReadAsStreamAsync(ct));
            if (batch is not null)
            {
                lock (delivered)
                    delivered.AddRange(batch.Events);
            }

            return new HttpResponseMessage(System.Net.HttpStatusCode.OK);
        });

        var switches = new SwitchSource();
        switches.WhenNotMatched(LogLevel.Warning);
        var options = new WatchOptions { FlushInterval = TimeSpan.FromMilliseconds(20) };

        using (var factory = LoggerFactory.Create(b =>
            b.AddWatch(CreateClient(handler), switches, new WatchDestination("http://localhost/D"), options, ownsDependencies: false)))
        {
            var logger = factory.CreateLogger("Orders.Service");

            using (new Activity("succeeds").Start())
            {
                RetroLogging.Begin();
                logger.LogDebug("quiet step");
            }

            using (new Activity("fails").Start())
            {
                RetroLogging.Begin();
                logger.LogDebug("step one");
                logger.LogDebug("step two");
                logger.LogObject(new { Secret = "Trace is never kept" });
                logger.LogError("boom");
            }
        }

        lock (delivered)
        {
            delivered.Select(e => e.Title).ShouldBe(["step one", "step two", "boom"]);
            delivered[0].Tag.ShouldBe("Retro");
            delivered[2].Tag.ShouldBeEmpty();
        }
    }

    private sealed class LevelCapture : ILoggerProvider
    {
        public List<LogLevel> Levels { get; } = [];

        public ILogger CreateLogger(string categoryName) => new Logger(this);

        public void Dispose()
        {
        }

        private sealed class Logger(LevelCapture owner) : ILogger
        {
            public IDisposable BeginScope<TState>(TState state) where TState : notnull => null;

            public bool IsEnabled(LogLevel logLevel) => true;

            public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception exception, Func<TState, Exception, string> formatter)
            {
                lock (owner.Levels)
                    owner.Levels.Add(logLevel);
            }
        }
    }
}
