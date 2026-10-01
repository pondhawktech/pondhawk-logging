// Copyright (c) Pond Hawk Technologies Inc. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using Shouldly;
using Xunit;

namespace Pondhawk.Logging.Watch.Tests;

public class WatchUrlTests
{
    [Theory]
    [InlineData("http://localhost:11000/orders", "http://localhost:11000/", "orders")]
    [InlineData("http://localhost:11000/orders/", "http://localhost:11000/", "orders")]
    [InlineData("https://watch.example/orders", "https://watch.example/", "orders")]
    [InlineData("https://ingest.example/watch/orders", "https://ingest.example/watch/", "orders")]
    [InlineData("https://ingest.example/a/b/orders", "https://ingest.example/a/b/", "orders")]
    [InlineData("http://localhost:11000/Orders%20Service", "http://localhost:11000/", "Orders Service")]
    public void Parse_SplitsBaseAndDomain(string url, string expectedBase, string expectedDomain)
    {
        var parsed = WatchUrl.Parse(url);

        parsed.BaseUri.ShouldBe(new Uri(expectedBase));
        parsed.Domain.ShouldBe(expectedDomain);
        parsed.ApiKey.ShouldBeNull();
        parsed.Redacted.ShouldNotContain("@");
    }

    [Fact]
    public void Parse_TakesTheKeyFromUserInfo_AndRedactsIt()
    {
        var parsed = WatchUrl.Parse("https://pwk_7f3kq2_Zx8vR1mQ9tL4bN6c@watch.example/orders");

        parsed.ApiKey.ShouldBe("pwk_7f3kq2_Zx8vR1mQ9tL4bN6c");
        parsed.BaseUri.ShouldBe(new Uri("https://watch.example/"));
        parsed.BaseUri.UserInfo.ShouldBeEmpty();
        parsed.Redacted.ShouldBe("https://***@watch.example/orders");
        parsed.ToString().ShouldBe(parsed.Redacted);
    }

    [Theory]
    [InlineData("http://pwk_a_s@localhost:11000/orders")]
    [InlineData("http://pwk_a_s@127.0.0.1:11000/orders")]
    [InlineData("http://pwk_a_s@[::1]:11000/orders")]
    public void Parse_AllowsAKeyOverHttp_ToLoopback(string url)
    {
        WatchUrl.Parse(url).ApiKey.ShouldBe("pwk_a_s");
    }

    [Fact]
    public void Parse_RefusesAKeyOverHttp_ToAnythingElse_WithoutEchoingTheKey()
    {
        var error = Should.Throw<ArgumentException>(() => WatchUrl.Parse("http://pwk_a_secret@watch.example/orders"));

        error.Message.ShouldContain("https");
        error.Message.ShouldNotContain("secret");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("  ")]
    [InlineData("localhost:11000/orders")]
    [InlineData("/orders")]
    [InlineData("ftp://watch.example/orders")]
    [InlineData("http://localhost:11000")]
    [InlineData("http://localhost:11000/")]
    [InlineData("http://localhost:11000/orders?x=1")]
    [InlineData("http://localhost:11000/orders#frag")]
    [InlineData("https://user:password@watch.example/orders")]
    public void Parse_RejectsInvalidUrls(string url)
    {
        Should.Throw<ArgumentException>(() => WatchUrl.Parse(url));
    }

    [Fact]
    public void Parse_RejectsUserAndPassword_WithoutEchoingThem()
    {
        var error = Should.Throw<ArgumentException>(() => WatchUrl.Parse("https://user:hunter2@watch.example/orders"));

        error.Message.ShouldNotContain("hunter2");
    }

    [Fact]
    public void Equality_ComparesBaseDomainAndKey()
    {
        WatchUrl.Parse("https://k@watch.example/orders").ShouldBe(WatchUrl.Parse("https://k@watch.example/orders/"));
        WatchUrl.Parse("https://k@watch.example/orders").ShouldNotBe(WatchUrl.Parse("https://other@watch.example/orders"));
        WatchUrl.Parse("https://watch.example/orders").ShouldNotBe(WatchUrl.Parse("https://watch.example/billing"));
        WatchUrl.Parse("https://watch.example/orders").ShouldNotBe(WatchUrl.Parse("https://watch.example/x/orders"));
    }
}
