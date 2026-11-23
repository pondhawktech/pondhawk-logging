// Copyright (c) Pond Hawk Technologies Inc. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System.Drawing;
using System.Net;
using BenchmarkDotNet.Attributes;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Pondhawk.Logging.Watch;

namespace Pondhawk.Logging.Benchmarks;

/// <summary>
/// The cost of a call that logs nothing: the category is below its level, so every call should be a no-op.
/// <see cref="Setup.NullLogger"/> is the floor — Fabrica.Watch's quiet logger, as near as MEL has one.
/// </summary>
/// <remarks>
/// Each benchmark is one call. Allocations include the Watch flush thread, which is idle here (nothing is queued).
/// </remarks>
[MemoryDiagnoser]
public class DisabledLoggingBenchmarks
{
    private const string Json = "{\"id\":1}";

    public enum Setup
    {
        /// <summary><c>NullLogger.Instance</c> and <c>NullLoggerFactory</c>: the floor.</summary>
        NullLogger,

        /// <summary>A MEL factory with no provider.</summary>
        NoProvider,

        /// <summary>Watch registered; the category's switch is <c>None</c> (Fabrica.Watch's <c>Quiet</c>).</summary>
        WatchSwitchNone,

        /// <summary>Watch registered; the category's switch is <c>Warning</c> — Debug dropped, the usual case.</summary>
        WatchSwitchWarning,
    }

    private ILoggerFactory _factory = null!;
    private ILogger _logger = null!;
    private readonly OrderService _instance = new();

    [Params(Setup.NullLogger, Setup.NoProvider, Setup.WatchSwitchNone, Setup.WatchSwitchWarning)]
    public Setup Scenario { get; set; }

    [GlobalSetup]
    public void GlobalSetup()
    {
        _factory = Scenario switch
        {
            Setup.NullLogger => NullLoggerFactory.Instance,
            Setup.NoProvider => LoggerFactory.Create(b => b.SetMinimumLevel(LogLevel.Warning)),
            Setup.WatchSwitchNone => WatchFactory(LogLevel.None),
            Setup.WatchSwitchWarning => WatchFactory(LogLevel.Warning),
            _ => throw new InvalidOperationException(),
        };

        _logger = _factory.CreateLogger(typeof(OrderService).FullName!);

        LoggingFactoryLocator.ResetForTesting();
        LoggingFactoryLocator.SetFactory(_factory);
    }

    [GlobalCleanup]
    public void GlobalCleanup()
    {
        LoggingFactoryLocator.ResetForTesting();
        _factory.Dispose();
    }

    [Benchmark(Description = "IsEnabled(Debug)")]
    public bool IsEnabled() => _logger.IsEnabled(LogLevel.Debug);

    [Benchmark(Description = "LogDebug(\"text\")")]
    public void LogDebug_Constant() => _logger.LogDebug("Order placed");

    [Benchmark(Description = "LogDebug(\"{Step}\", i)")]
    public void LogDebug_Template() => _logger.LogDebug("Step {Step}", 3);

    [Benchmark(Description = "Inspect(name, value)")]
    public void Inspect() => _logger.Inspect("subtotal", 30);

    [Benchmark(Description = "LogJson(title, json)")]
    public void LogJson() => _logger.LogJson("Order", Json);

    [Benchmark(Description = "using logger.EnterMethod()")]
    public void EnterMethod_Logger()
    {
        using var method = _logger.EnterMethod("PlaceOrder");
    }

    [Benchmark(Description = "this.GetLogger()")]
    public ILogger GetLogger_Object() => _instance.GetLogger();

    [Benchmark(Description = "using this.EnterMethod()")]
    public void EnterMethod_Object()
    {
        using var method = _instance.EnterMethod("PlaceOrder");
    }

    private static ILoggerFactory WatchFactory(LogLevel level)
    {
        // A small table, so the lookup is the longest-prefix match an application has, not a lone default.
        var switches = new SwitchSource();
        switches.WhenNotMatched(LogLevel.Warning);
        switches.WhenMatched("Microsoft", LogLevel.Warning, Color.White);
        switches.WhenMatched("Pondhawk", LogLevel.Information, Color.White);
        switches.WhenMatched("Pondhawk.Logging.Benchmarks", level, Color.White);
        switches.WhenMatched("Pondhawk.Logging.Benchmarks.Other", LogLevel.Debug, Color.White);

        return LoggerFactory.Create(b =>
            b.AddWatch(Client(), switches, new WatchDestination("http://localhost/Bench"), new WatchOptions(), ownsDependencies: true));
    }

    private static HttpClient Client() => new(new OkHandler());

    private sealed class OkHandler : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken) =>
            Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK));
    }
}

/// <summary>The instance behind <c>this.GetLogger()</c>; its type names the category.</summary>
public sealed class OrderService;
