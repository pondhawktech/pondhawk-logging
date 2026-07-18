// Copyright (c) Pond Hawk Technologies Inc. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using Pondhawk.Logging.Tests.Support;
using Shouldly;
using Xunit;

namespace Pondhawk.Logging.Tests;

// Kept in one class: the locator is process-wide static, so Reset() in the ctor/Dispose isolates each
// test and these do not race a parallel test class (only this class touches the locator).
public class LoggingFactoryLocatorTests : IDisposable
{
    public LoggingFactoryLocatorTests() => LoggingFactoryLocator.Reset();

    public void Dispose() => LoggingFactoryLocator.Reset();

    [Fact]
    public void SetFactory_MayBeCalledOnce_SecondCallThrows()
    {
        LoggingFactoryLocator.SetFactory(new RecordingLoggerFactory());

        Should.Throw<InvalidOperationException>(
            () => LoggingFactoryLocator.SetFactory(new RecordingLoggerFactory()));
    }

    [Fact]
    public void GetFactory_BeforeSet_Throws()
    {
        Should.Throw<InvalidOperationException>(() => LoggingFactoryLocator.GetFactory());
    }

    [Fact]
    public void GetFactory_ReturnsTheSetFactory()
    {
        var factory = new RecordingLoggerFactory();
        LoggingFactoryLocator.SetFactory(factory);

        LoggingFactoryLocator.GetFactory().ShouldBeSameAs(factory);
    }

    [Fact]
    public void GetLogger_UsesConciseFullNameOfInstanceType()
    {
        var factory = new RecordingLoggerFactory();
        LoggingFactoryLocator.SetFactory(factory);

        var logger = new LocatorSample().GetLogger();

        logger.ShouldNotBeNull();
        factory.Categories.ShouldContain("Pondhawk.Logging.Tests.LocatorSample");
    }

    [Fact]
    public void EnterMethod_OnObject_LogsEnteringViaTypeLogger()
    {
        var factory = new RecordingLoggerFactory();
        LoggingFactoryLocator.SetFactory(factory);

        using (new LocatorSample().EnterMethod())
        {
        }

        factory.Categories.ShouldContain("Pondhawk.Logging.Tests.LocatorSample");
        factory.Logger.Entries.ShouldContain(e => e.Message.StartsWith("Entering"));
    }
}

internal sealed class LocatorSample
{
}
