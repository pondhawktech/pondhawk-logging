// Copyright (c) Pond Hawk Technologies Inc. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using Shouldly;
using Xunit;

namespace Pondhawk.Logging.Watch.Tests;

public class WatchDestinationTests
{
    [Fact]
    public void Constructor_ExposesServerUrlDomainAndAVersion()
    {
        var destination = new WatchDestination("http://localhost:11000", "MyApp");

        destination.ServerUrl.ShouldBe("http://localhost:11000");
        destination.Domain.ShouldBe("MyApp");
        destination.Version.ShouldBe(1);
    }

    [Theory]
    [InlineData(null, "D")]
    [InlineData("", "D")]
    [InlineData("   ", "D")]
    [InlineData("http://localhost:11000", null)]
    [InlineData("http://localhost:11000", "")]
    [InlineData("http://localhost:11000", "   ")]
    public void Constructor_RejectsMissingServerUrlOrDomain(string serverUrl, string domain)
    {
        Should.Throw<ArgumentException>(() => new WatchDestination(serverUrl, domain));
    }

    [Fact]
    public void BuildsSinkAndSwitchUris_NormalizingTheTrailingSlash()
    {
        var withSlash = new WatchDestination("http://localhost:11000/", "MyApp").Current;
        var withoutSlash = new WatchDestination("http://localhost:11000", "MyApp").Current;

        withSlash.SinkUri.ShouldBe(new Uri("http://localhost:11000/api/sink"));
        withoutSlash.SinkUri.ShouldBe(withSlash.SinkUri);
        withSlash.SwitchesUri.ShouldBe(new Uri("http://localhost:11000/api/switches?domain=MyApp"));
    }

    [Fact]
    public void BuildsSwitchUri_EscapingTheDomain()
    {
        var destination = new WatchDestination("http://localhost:11000", "my domain/special");

        destination.Current.SwitchesUri.PathAndQuery.ShouldContain("domain=my%20domain%2Fspecial");
    }

    [Fact]
    public void Rebind_MovesServerAndDomain_AndBumpsTheVersion()
    {
        var destination = new WatchDestination("http://first.example", "A");

        destination.Rebind("http://second.example", "B").ShouldBeTrue();

        destination.ServerUrl.ShouldBe("http://second.example");
        destination.Domain.ShouldBe("B");
        destination.Version.ShouldBe(2);
        destination.Current.SinkUri.ShouldBe(new Uri("http://second.example/api/sink"));
        destination.Current.SwitchesUri.ShouldBe(new Uri("http://second.example/api/switches?domain=B"));
    }

    [Fact]
    public void Rebind_ToTheSameDestination_ChangesNothing()
    {
        // Configuration that repeats the current destination is the common case for a client polling for
        // it, so an unchanged rebind must not disturb switch polling or the circuit breaker.
        var destination = new WatchDestination("http://first.example", "A");

        destination.Rebind("http://first.example", "A").ShouldBeFalse();

        destination.Version.ShouldBe(1);
    }

    [Theory]
    [InlineData(null, "D")]
    [InlineData("", "D")]
    [InlineData("http://second.example", null)]
    [InlineData("http://second.example", "")]
    public void Rebind_RejectsMissingServerUrlOrDomain(string serverUrl, string domain)
    {
        var destination = new WatchDestination("http://first.example", "A");

        Should.Throw<ArgumentException>(() => destination.Rebind(serverUrl, domain));
    }

    // ── Unbound ──

    [Fact]
    public void Unbound_NamesNoServer_AndHasNowhereToPostOrPoll()
    {
        var destination = WatchDestination.Unbound("agent");

        destination.IsBound.ShouldBeFalse();
        destination.ServerUrl.ShouldBeEmpty();
        destination.Domain.ShouldBe("agent");
        destination.Current.SinkUri.ShouldBeNull();
        destination.Current.SwitchesUri.ShouldBeNull();
    }

    [Fact]
    public void Unbound_TakesNoDomain_WhenThereIsNothingToLabelItWith()
    {
        WatchDestination.Unbound().Domain.ShouldBeEmpty();
    }

    [Fact]
    public void Unbound_IsRebindable_AndBecomesBound()
    {
        var destination = WatchDestination.Unbound("agent");

        destination.Rebind("http://watch.example", "Fleet").ShouldBeTrue();

        destination.IsBound.ShouldBeTrue();
        destination.ServerUrl.ShouldBe("http://watch.example");
        destination.Domain.ShouldBe("Fleet");
        destination.Version.ShouldBe(2);
        destination.Current.SinkUri.ShouldBe(new Uri("http://watch.example/api/sink"));
    }

    [Fact]
    public void RelativeDestination_KeepsUrisRelative_AndCannotBeRebound()
    {
        // The shape behind the domain-only constructors: URIs resolve against the client's base address,
        // so there is no server URL of ours to replace.
        var destination = new WatchDestination("MyApp");

        destination.ServerUrl.ShouldBeEmpty();
        destination.Current.SinkUri.IsAbsoluteUri.ShouldBeFalse();
        destination.Current.SinkUri.ToString().ShouldBe("api/sink");

        Should.Throw<InvalidOperationException>(() => destination.Rebind("http://second.example", "B"));
    }
}
