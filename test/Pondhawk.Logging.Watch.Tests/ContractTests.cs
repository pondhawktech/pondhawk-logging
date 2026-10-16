// Copyright (c) Pond Hawk Technologies Inc. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System.Diagnostics;
using Shouldly;
using Xunit;

namespace Pondhawk.Logging.Watch.Tests;

/// <summary>
/// The provider does not reference Pondhawk.Logging — an application references it from NuGet, vendors its source, or drops it — so it keeps its own
/// copy of what the API writes. These check the copy against the API.
/// </summary>
public class ContractTests
{
    [Fact]
    public void TheLogStateNames_AreTheApis()
    {
        LogStateContract.Nesting.ShouldBe(LogPropertyNames.Nesting);
        LogStateContract.PayloadType.ShouldBe(LogPropertyNames.PayloadType);
        LogStateContract.PayloadContent.ShouldBe(LogPropertyNames.PayloadContent);
        LogStateContract.ErrorContext.ShouldBe(LogPropertyNames.ErrorContext);
    }

    [Fact]
    public void ThePayloadTypes_AreTheApis()
    {
        LogStateContract.JsonPayload.ShouldBe((int)PayloadType.Json);
        LogStateContract.TextPayload.ShouldBe((int)PayloadType.Text);
    }

    [Fact]
    public void AProbeTheApiBegins_IsSeen()
    {
        ProbeContract.Tag.ShouldBe(DiagnosticProbe.Tag);

        using (new Activity("probed").Start())
        {
            ProbeContract.IsActive.ShouldBeFalse();
            DiagnosticProbe.Begin().ShouldBeTrue();

            using (new Activity("inner").Start())
                ProbeContract.IsActive.ShouldBeTrue();
        }

        ProbeContract.IsActive.ShouldBeFalse();
    }

    [Fact]
    public void TheProvider_DoesNotReferencePondhawkLogging() =>
        typeof(WatchLoggerProcessor).Assembly.GetReferencedAssemblies()
            .Select(a => a.Name)
            .ShouldNotContain("Pondhawk.Logging");
}
