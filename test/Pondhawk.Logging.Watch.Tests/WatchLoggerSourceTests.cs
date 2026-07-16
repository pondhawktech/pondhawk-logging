// Copyright (c) Pond Hawk Technologies Inc. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using Pondhawk.Logging;
using Serilog;
using Serilog.Events;
using Shouldly;
using Xunit;

namespace Pondhawk.Logging.Watch.Tests;

public class WatchLoggerSourceTests
{
    private static SwitchSource Switches(LogEventLevel defaultLevel)
    {
        var source = new SwitchSource();
        source.WhenNotMatched(defaultLevel);
        return source;
    }

    [Fact]
    public void CreateLogger_ReturnsSwitchAwareLogger()
    {
        var root = new LoggerConfiguration().MinimumLevel.Verbose().CreateLogger();
        var source = new WatchLoggerSource(root, Switches(LogEventLevel.Warning));

        var logger = source.CreateLogger("MyApp.Thing");

        logger.IsEnabled(LogEventLevel.Verbose).ShouldBeFalse();
        logger.IsEnabled(LogEventLevel.Warning).ShouldBeTrue();
    }

    [Fact]
    public void Ctor_NullRoot_Throws()
    {
        Should.Throw<ArgumentNullException>(() => new WatchLoggerSource(null, Switches(LogEventLevel.Warning)));
    }

    [Fact]
    public void Ctor_NullSwitches_Throws()
    {
        var root = new LoggerConfiguration().CreateLogger();
        Should.Throw<ArgumentNullException>(() => new WatchLoggerSource(root, null));
    }
}
