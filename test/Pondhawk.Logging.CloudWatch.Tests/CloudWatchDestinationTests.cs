// Copyright (c) Pond Hawk Technologies Inc. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using Shouldly;
using Xunit;

namespace Pondhawk.Logging.CloudWatch.Tests;

public class CloudWatchDestinationTests
{
    [Fact]
    public void ABoundDestination_NamesItsGroup()
    {
        var destination = new CloudWatchDestination("/pondhawk-one/PartnerConnect-Production");

        destination.LogGroup.ShouldBe("/pondhawk-one/PartnerConnect-Production");
        destination.IsBound.ShouldBeTrue();
        destination.Version.ShouldBe(1);
    }

    [Fact]
    public void AnUnboundDestination_NamesNothing_UntilRebound()
    {
        var destination = CloudWatchDestination.Unbound();

        destination.IsBound.ShouldBeFalse();
        destination.LogGroup.ShouldBeEmpty();

        destination.Rebind("/pondhawk-one/A").ShouldBeTrue();

        destination.IsBound.ShouldBeTrue();
        destination.LogGroup.ShouldBe("/pondhawk-one/A");
        destination.Version.ShouldBe(2);
    }

    [Fact]
    public void RebindingToTheSameGroup_DisturbsNothing()
    {
        var destination = new CloudWatchDestination("/pondhawk-one/A");

        destination.Rebind("/pondhawk-one/A").ShouldBeFalse();
        destination.Version.ShouldBe(1);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("has space")]
    [InlineData("has:colon")]
    [InlineData("has*star")]
    public void AnInvalidGroupName_IsRefused(string logGroup)
    {
        Should.Throw<ArgumentException>(() => new CloudWatchDestination(logGroup));
        Should.Throw<ArgumentException>(() => CloudWatchDestination.Unbound().Rebind(logGroup));
    }

    [Fact]
    public void AGroupNameLongerThanCloudWatchAllows_IsRefused()
    {
        Should.Throw<ArgumentException>(() => new CloudWatchDestination(new string('a', 513)));
    }

    [Theory]
    [InlineData("/fabrica-one/PartnerConnect-Production", "/fabrica-one/PartnerConnect-Production")]
    [InlineData("/pondhawk-one/Partner Connect-UAT", "/pondhawk-one/Partner-Connect-UAT")]
    [InlineData("a:b*c", "a-b-c")]
    public void Sanitize_ReplacesWhatAGroupNameMayNotContain(string text, string expected)
    {
        CloudWatchDestination.Sanitize(text).ShouldBe(expected);
        new CloudWatchDestination(CloudWatchDestination.Sanitize(text)).LogGroup.ShouldBe(expected);
    }
}
