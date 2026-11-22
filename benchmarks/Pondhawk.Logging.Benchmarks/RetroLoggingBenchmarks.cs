// Copyright (c) Pond Hawk Technologies Inc. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System.Diagnostics;
using System.Net;
using BenchmarkDotNet.Attributes;
using Microsoft.Extensions.Logging;
using Pondhawk.Logging;
using Pondhawk.Logging.Watch;
using ZLogger;

namespace Pondhawk.Logging.Benchmarks;

/// <summary>
/// The cost of retro logging per request. Each benchmark runs one simulated request in its own
/// <see cref="Activity"/>: a method trace, ten Debug messages, ten <c>Inspect</c> calls and a SQL payload — 23 Debug
/// events, all below the switch (Warning). <see cref="Unmarked_Baseline"/> uses the switch filter as 5.0.0 had it,
/// with no retro clause.
/// </summary>
/// <remarks>
/// Allocations include the background flush thread (BenchmarkDotNet counts the whole process), so a request that
/// sends events also pays for their serialization and post — to a handler that answers at once.
/// </remarks>
[MemoryDiagnoser]
public class RetroLoggingBenchmarks
{
    private const string Sql = "select o.id, o.total from orders o where o.customer_id = @customer";

    private ILoggerFactory _baselineFactory = null!;
    private ILoggerFactory _factory = null!;
    private ILogger _baseline = null!;
    private ILogger _logger = null!;

    [GlobalSetup]
    public void Setup()
    {
        var baselineSwitches = Switches();
        _baselineFactory = LoggerFactory.Create(b =>
        {
            b.SetMinimumLevel(LogLevel.Trace);
            b.AddFilter((category, level) =>
                string.IsNullOrWhiteSpace(category)
                || level >= baselineSwitches.Lookup(category).Level
                || (level < LogLevel.None && DiagnosticProbe.IsActive));
            b.AddProvider(new WatchLoggerProvider(
                new WatchLoggerProcessor(Client(), baselineSwitches, Destination()),
                new ZLoggerOptions()));
        });
        _baseline = _baselineFactory.CreateLogger("Orders.Service");

        _factory = LoggerFactory.Create(b =>
            b.AddWatch(Client(), Switches(), Destination(), new WatchOptions(), ownsDependencies: true));
        _logger = _factory.CreateLogger("Orders.Service");
    }

    [GlobalCleanup]
    public void Cleanup()
    {
        _baselineFactory.Dispose();
        _factory.Dispose();
    }

    [Benchmark(Baseline = true, Description = "Unmarked, 5.0.0 filter")]
    public void Unmarked_Baseline() => Request(_baseline, marked: false, fails: false);

    [Benchmark(Description = "Unmarked")]
    public void Unmarked() => Request(_logger, marked: false, fails: false);

    [Benchmark(Description = "Marked, succeeds")]
    public void Marked_Succeeds() => Request(_logger, marked: true, fails: false);

    [Benchmark(Description = "Marked, fails")]
    public void Marked_Fails() => Request(_logger, marked: true, fails: true);

    private static void Request(ILogger logger, bool marked, bool fails)
    {
        using var activity = new Activity("request").Start();
        if (marked)
            RetroLogging.Begin();

        using (logger.EnterMethod("PlaceOrder"))
        {
            for (var i = 0; i < 10; i++)
            {
                logger.LogDebug("Step {Step}", i);
                logger.Inspect("subtotal", i * 10);
            }

            logger.LogSql("Query", Sql);

            if (fails)
                logger.LogError("Order failed");
        }
    }

    private static SwitchSource Switches()
    {
        var switches = new SwitchSource();
        switches.WhenNotMatched(LogLevel.Warning);
        return switches;
    }

    private static WatchDestination Destination() => new("http://localhost/Bench");

    private static HttpClient Client() => new(new OkHandler());

    private sealed class OkHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
    }
}
