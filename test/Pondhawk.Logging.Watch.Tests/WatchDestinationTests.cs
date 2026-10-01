// Copyright (c) Pond Hawk Technologies Inc. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using Shouldly;
using Xunit;

namespace Pondhawk.Logging.Watch.Tests;

public class WatchDestinationTests
{
    [Fact]
    public void Constructor_ExposesUrlDomainAndAVersion()
    {
        var destination = new WatchDestination("http://localhost:11000/MyApp");

        destination.Url.ShouldBe("http://localhost:11000/MyApp");
        destination.Domain.ShouldBe("MyApp");
        destination.Version.ShouldBe(1);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("http://localhost:11000")]
    [InlineData("http://localhost:11000/")]
    public void Constructor_RejectsAMissingUrlOrDomain(string watchUrl)
    {
        Should.Throw<ArgumentException>(() => new WatchDestination(watchUrl));
    }

    [Fact]
    public void BuildsSinkAndSwitchUris_NormalizingTheTrailingSlash()
    {
        var withSlash = new WatchDestination("http://localhost:11000/MyApp/").Current;
        var withoutSlash = new WatchDestination("http://localhost:11000/MyApp").Current;

        withSlash.SinkUri.ShouldBe(new Uri("http://localhost:11000/api/sink"));
        withoutSlash.SinkUri.ShouldBe(withSlash.SinkUri);
        withSlash.SwitchesUri.ShouldBe(new Uri("http://localhost:11000/api/switches?domain=MyApp"));
    }

    [Fact]
    public void BuildsUris_UnderABasePath()
    {
        var binding = new WatchDestination("https://ingest.example/watch/MyApp").Current;

        binding.SinkUri.ShouldBe(new Uri("https://ingest.example/watch/api/sink"));
        binding.SwitchesUri.ShouldBe(new Uri("https://ingest.example/watch/api/switches?domain=MyApp"));
    }

    [Fact]
    public void BuildsSwitchUri_EscapingTheDomain()
    {
        var destination = new WatchDestination("http://localhost:11000/my%20domain%2Fspecial");

        destination.Domain.ShouldBe("my domain/special");
        destination.Current.SwitchesUri.PathAndQuery.ShouldContain("domain=my%20domain%2Fspecial");
    }

    [Fact]
    public void Key_IsKeptOutOfTheUrisAndTheDisplayedUrl()
    {
        var destination = new WatchDestination("https://pwk_a1_secret@watch.example/MyApp");

        destination.Current.ApiKey.ShouldBe("pwk_a1_secret");
        destination.Url.ShouldBe("https://***@watch.example/MyApp");
        destination.Current.SinkUri.UserInfo.ShouldBeEmpty();
        destination.Current.SwitchesUri.UserInfo.ShouldBeEmpty();
    }

    [Fact]
    public void Rebind_MovesServerAndDomain_AndBumpsTheVersion()
    {
        var destination = new WatchDestination("http://first.example/A");

        destination.Rebind("http://second.example/B").ShouldBeTrue();

        destination.Url.ShouldBe("http://second.example/B");
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
        var destination = new WatchDestination("https://pwk_a1_s@first.example/A");

        destination.Rebind("https://pwk_a1_s@first.example/A/").ShouldBeFalse();

        destination.Version.ShouldBe(1);
    }

    [Fact]
    public void Rebind_WithOnlyANewKey_IsAChange()
    {
        var destination = new WatchDestination("https://pwk_a1_old@first.example/A");

        destination.Rebind("https://pwk_a2_new@first.example/A").ShouldBeTrue();

        destination.Version.ShouldBe(2);
        destination.Current.ApiKey.ShouldBe("pwk_a2_new");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("http://second.example")]
    public void Rebind_RejectsAnInvalidUrl_AndKeepsTheCurrentOne(string watchUrl)
    {
        var destination = new WatchDestination("http://first.example/A");

        Should.Throw<ArgumentException>(() => destination.Rebind(watchUrl));

        destination.Version.ShouldBe(1);
        destination.Domain.ShouldBe("A");
    }

    // ── Unbound ──

    [Fact]
    public void Unbound_NamesNoServer_AndHasNowhereToPostOrPoll()
    {
        var destination = WatchDestination.Unbound();

        destination.IsBound.ShouldBeFalse();
        destination.Url.ShouldBeEmpty();
        destination.Domain.ShouldBeEmpty();
        destination.Current.SinkUri.ShouldBeNull();
        destination.Current.SwitchesUri.ShouldBeNull();
        destination.Current.ApiKey.ShouldBeNull();
    }

    [Fact]
    public void Unbound_IsRebindable_AndBecomesBound()
    {
        var destination = WatchDestination.Unbound();

        destination.Rebind("http://watch.example/Fleet").ShouldBeTrue();

        destination.IsBound.ShouldBeTrue();
        destination.Url.ShouldBe("http://watch.example/Fleet");
        destination.Domain.ShouldBe("Fleet");
        destination.Version.ShouldBe(2);
        destination.Current.SinkUri.ShouldBe(new Uri("http://watch.example/api/sink"));
    }

    [Fact]
    public void RelativeDestination_KeepsUrisRelative_AndCannotBeRebound()
    {
        // The shape behind the domain-only constructors: URIs resolve against the client's base address,
        // so there is no server URL of ours to replace.
        var destination = WatchDestination.Relative("MyApp");

        destination.Url.ShouldBeEmpty();
        destination.Current.ApiKey.ShouldBeNull();
        destination.Current.SinkUri.IsAbsoluteUri.ShouldBeFalse();
        destination.Current.SinkUri.ToString().ShouldBe("api/sink");

        Should.Throw<InvalidOperationException>(() => destination.Rebind("http://second.example/B"));
    }
}
