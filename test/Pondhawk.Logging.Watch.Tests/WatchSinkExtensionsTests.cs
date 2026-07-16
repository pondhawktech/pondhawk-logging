// Copyright (c) Pond Hawk Technologies Inc. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System.Drawing;
using Serilog;
using Serilog.Events;
using Shouldly;
using Xunit;

namespace Pondhawk.Logging.Watch.Tests;

public class WatchSinkExtensionsTests
{
    // ── UseWatch ──

    [Fact]
    public void UseWatch_SetsMinimumLevelVerbose_AndAddsSink()
    {
        var config = new LoggerConfiguration().UseWatch("http://localhost:11000", "TestApp");
        using var logger = config.CreateLogger();

        // MinimumLevel.Verbose() means every level is enabled; the Watch Server drives filtering.
        logger.IsEnabled(LogEventLevel.Verbose).ShouldBeTrue();
    }

    [Fact]
    public void UseWatch_WithOptions_AppliesConfiguration()
    {
        var config = new LoggerConfiguration().UseWatch("http://localhost:11000", "TestApp", o =>
        {
            o.BatchSize = 25;
            o.DefaultLevel = LogEventLevel.Error;
        });
        using var logger = config.CreateLogger();

        logger.IsEnabled(LogEventLevel.Verbose).ShouldBeTrue();
    }

    [Fact]
    public void UseWatch_NullConfig_Throws()
    {
        LoggerConfiguration config = null!;

        Should.Throw<ArgumentNullException>(
            () => config.UseWatch("http://localhost:11000", "TestApp"));
    }

    // ── WriteTo.Watch guards ──

    [Fact]
    public void Watch_NullServerUrl_Throws()
    {
        Should.Throw<ArgumentException>(
            () => new LoggerConfiguration().WriteTo.Watch(null!, "domain"));
    }

    [Fact]
    public void Watch_WhitespaceServerUrl_Throws()
    {
        Should.Throw<ArgumentException>(
            () => new LoggerConfiguration().WriteTo.Watch("   ", "domain"));
    }

    [Fact]
    public void Watch_NullDomain_Throws()
    {
        Should.Throw<ArgumentException>(
            () => new LoggerConfiguration().WriteTo.Watch("http://localhost:11000", null!));
    }

    [Fact]
    public void Watch_NullConfigureAction_Throws()
    {
        Should.Throw<ArgumentNullException>(
            () => new LoggerConfiguration().WriteTo.Watch("http://localhost:11000", "domain", null!));
    }

    // ── Low-level WatchSink overload (offline; caller-supplied dependencies) ──

    [Fact]
    public void WatchSink_LowLevel_AddsWorkingSink()
    {
        using var http = new HttpClient { BaseAddress = new Uri("http://localhost:11000") };
        var switchSource = new SwitchSource();
        switchSource.WhenNotMatched(LogEventLevel.Verbose);

        using var logger = new LoggerConfiguration()
            .MinimumLevel.Verbose()
            .WriteTo.WatchSink(http, switchSource, "Test")
            .CreateLogger();

        logger.IsEnabled(LogEventLevel.Verbose).ShouldBeTrue();
    }

    // ── WatchSinkOptions defaults ──

    [Fact]
    public void WatchSinkOptions_HasExpectedDefaults()
    {
        var o = new WatchSinkOptions();

        o.ServerUrl.ShouldBe("http://localhost:11000");
        o.Domain.ShouldBe("Default");
        o.DefaultLevel.ShouldBe(LogEventLevel.Warning);
        o.DefaultColor.ShouldBe(Color.LightGray);
        o.BatchSize.ShouldBe(100);
        o.FlushInterval.ShouldBeNull();
        o.PollInterval.ShouldBeNull();
    }
}
