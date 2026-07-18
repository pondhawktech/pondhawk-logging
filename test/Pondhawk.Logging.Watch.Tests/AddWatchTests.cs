// Copyright (c) Pond Hawk Technologies Inc. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System.Drawing;
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
        var options = new WatchOptions { Domain = "D" };
        return LoggerFactory.Create(b =>
            b.AddWatch(CreateClient(new MockHttpHandler()), switches, options, ownsDependencies: false));
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
}
