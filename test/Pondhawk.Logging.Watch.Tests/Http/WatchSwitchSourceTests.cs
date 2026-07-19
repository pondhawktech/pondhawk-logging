// Copyright (c) Pond Hawk Technologies Inc. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System.Net;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Shouldly;
using Xunit;

namespace Pondhawk.Logging.Watch.Tests.Http;

public class WatchSwitchSourceTests
{

    private static HttpClient CreateClient(MockHttpHandler handler)
    {
        return new HttpClient(handler) { BaseAddress = new Uri("http://localhost:11000") };
    }

    private static StringContent CreateSwitchesJson(params SwitchDto[] switches)
    {
        var response = new SwitchesResponse { Switches = [.. switches] };
        var json = JsonSerializer.Serialize(response);
        return new StringContent(json, Encoding.UTF8, "application/json");
    }

    // --- Constructor ---

    [Fact]
    public void Constructor_NullClient_Throws()
    {
        Should.Throw<ArgumentNullException>(() => new WatchSwitchSource(null, "domain"));
    }

    [Fact]
    public void Constructor_NullDomain_Throws()
    {
        var handler = new MockHttpHandler();
        var client = CreateClient(handler);

        Should.Throw<ArgumentNullException>(() => new WatchSwitchSource(client, null));
    }

    // --- Defaults ---

    [Fact]
    public void PollingEnabled_DefaultsToTrue()
    {
        var handler = new MockHttpHandler();
        var source = new WatchSwitchSource(CreateClient(handler), "test");

        source.PollingEnabled.ShouldBeTrue();
    }

    [Fact]
    public void InheritsSwitchSource_DefaultVersion()
    {
        var handler = new MockHttpHandler();
        var source = new WatchSwitchSource(CreateClient(handler), "test");

        source.Version.ShouldBe(0);
    }

    // --- UpdateAsync ---

    [Fact]
    public async Task UpdateAsync_FetchesSwitchesFromServer()
    {
        var handler = new MockHttpHandler();
        handler.RespondWith(HttpStatusCode.OK, CreateSwitchesJson(
            new SwitchDto { Pattern = "MyApp", Tag = "app", Level = (int)LogLevel.Debug, Color = System.Drawing.Color.Green.ToArgb() }
        ));

        var source = new WatchSwitchSource(CreateClient(handler), "my-domain");

        await source.UpdateAsync();

        handler.Requests.Count.ShouldBe(1);
        handler.Requests[0].RequestUri.PathAndQuery.ShouldContain("api/switches");
        handler.Requests[0].RequestUri.PathAndQuery.ShouldContain("domain=my-domain");
    }

    [Fact]
    public async Task UpdateAsync_ParsesSwitchesAndCallsUpdate()
    {
        var handler = new MockHttpHandler();
        handler.RespondWith(HttpStatusCode.OK, CreateSwitchesJson(
            new SwitchDto { Pattern = "MyApp.Services", Tag = "svc", Level = (int)LogLevel.Debug, Color = System.Drawing.Color.Blue.ToArgb() }
        ));

        var source = new WatchSwitchSource(CreateClient(handler), "test");

        await source.UpdateAsync();

        source.Version.ShouldBe(1);
        var sw = source.Lookup("MyApp.Services.Repository");
        sw.Pattern.ShouldBe("MyApp.Services");
        sw.Tag.ShouldBe("svc");
        sw.Level.ShouldBe(LogLevel.Debug);
    }

    [Fact]
    public async Task UpdateAsync_MultipleSwitches_AllParsed()
    {
        var handler = new MockHttpHandler();
        handler.RespondWith(HttpStatusCode.OK, CreateSwitchesJson(
            new SwitchDto { Pattern = "MyApp", Level = (int)LogLevel.Warning, Color = 0 },
            new SwitchDto { Pattern = "MyApp.Data", Level = (int)LogLevel.Debug, Color = 0 }
        ));

        var source = new WatchSwitchSource(CreateClient(handler), "test");

        await source.UpdateAsync();

        // Longest prefix match
        source.Lookup("MyApp.Data.Sql").Level.ShouldBe(LogLevel.Debug);
        source.Lookup("MyApp.Services").Level.ShouldBe(LogLevel.Warning);
    }

    [Fact]
    public async Task UpdateAsync_LevelAboveCritical_ClampsToCritical()
    {
        var handler = new MockHttpHandler();
        handler.RespondWith(HttpStatusCode.OK, CreateSwitchesJson(
            new SwitchDto { Pattern = "HighLevel", Level = 999, Color = 0 }
        ));

        var source = new WatchSwitchSource(CreateClient(handler), "test");

        await source.UpdateAsync();

        // Level > 5 should clamp to Critical
        source.Lookup("HighLevel.Something").Level.ShouldBe(LogLevel.Critical);
    }

    [Fact]
    public async Task UpdateAsync_IncrementsVersion()
    {
        var handler = new MockHttpHandler();
        handler.RespondWith(HttpStatusCode.OK, CreateSwitchesJson(
            new SwitchDto { Pattern = "P1", Level = 0, Color = 0 }
        ));

        var source = new WatchSwitchSource(CreateClient(handler), "test");
        source.Version.ShouldBe(0);

        await source.UpdateAsync();
        source.Version.ShouldBe(1);

        await source.UpdateAsync();
        source.Version.ShouldBe(2);
    }

    [Fact]
    public async Task UpdateAsync_ServerError_DoesNotThrow()
    {
        var handler = new MockHttpHandler();
        handler.RespondWith(HttpStatusCode.InternalServerError);

        var source = new WatchSwitchSource(CreateClient(handler), "test");

        // Should silently swallow the error
        await source.UpdateAsync();
    }

    [Fact]
    public async Task UpdateAsync_NetworkError_DoesNotThrow()
    {
        var handler = new MockHttpHandler();
        handler.ThrowOnSend(new HttpRequestException("connection refused"));

        var source = new WatchSwitchSource(CreateClient(handler), "test");

        // Should silently swallow the error
        await source.UpdateAsync();
    }

    [Fact]
    public async Task UpdateAsync_NullSwitchesInResponse_DoesNotThrow()
    {
        var handler = new MockHttpHandler();
        var json = JsonSerializer.Serialize(new { Switches = (object)null });
        handler.RespondWith(HttpStatusCode.OK, new StringContent(json, Encoding.UTF8, "application/json"));

        var source = new WatchSwitchSource(CreateClient(handler), "test");

        await source.UpdateAsync();
        source.Version.ShouldBe(0); // No update occurred
    }

    [Fact]
    public async Task UpdateAsync_FailurePreservesExistingSwitches()
    {
        var handler = new MockHttpHandler();
        handler.RespondWith(HttpStatusCode.OK, CreateSwitchesJson(
            new SwitchDto { Pattern = "Existing", Level = (int)LogLevel.Debug, Color = 0 }
        ));

        var source = new WatchSwitchSource(CreateClient(handler), "test");
        await source.UpdateAsync();

        source.Lookup("Existing.Sub").Level.ShouldBe(LogLevel.Debug);

        // Now fail
        handler.RespondWith(HttpStatusCode.InternalServerError);
        await source.UpdateAsync();

        // Existing switches should be unchanged
        source.Lookup("Existing.Sub").Level.ShouldBe(LogLevel.Debug);
    }

    // --- Start ---

    [Fact]
    public async Task Start_DoesInitialFetch()
    {
        var handler = new MockHttpHandler();
        handler.RespondWith(HttpStatusCode.OK, CreateSwitchesJson(
            new SwitchDto { Pattern = "App", Level = (int)LogLevel.Information, Color = 0 }
        ));

        using var source = new WatchSwitchSource(CreateClient(handler), "test")
        {
            PollingEnabled = false // disable polling to avoid background tasks
        };

        source.Start();

        // Wait for the async poll loop to complete the initial fetch
        await Task.Delay(200);

        handler.Requests.Count.ShouldBe(1);
        source.Version.ShouldBe(1);
    }

    [Fact]
    public async Task Start_IsIdempotent()
    {
        var handler = new MockHttpHandler();
        handler.RespondWith(HttpStatusCode.OK, CreateSwitchesJson());

        using var source = new WatchSwitchSource(CreateClient(handler), "test")
        {
            PollingEnabled = false
        };

        source.Start();
        source.Start();
        source.Start();

        // Wait for the async poll loop to complete the initial fetch
        await Task.Delay(200);

        // Only one HTTP request should be made (initial fetch)
        handler.Requests.Count.ShouldBe(1);
    }

    [Fact]
    public async Task Start_WithPolling_StartsPollTask()
    {
        var handler = new MockHttpHandler();
        handler.RespondWith(HttpStatusCode.OK, CreateSwitchesJson());

        using var source = new WatchSwitchSource(CreateClient(handler), "test", TimeSpan.FromMilliseconds(50))
        {
            PollingEnabled = true
        };

        source.Start();

        // Wait for at least one poll cycle
        await Task.Delay(300);

        // Should have initial fetch + at least one poll
        handler.Requests.Count.ShouldBeGreaterThan(1);
    }

    // --- Stop ---

    [Fact]
    public async Task Stop_StopsPolling()
    {
        var handler = new MockHttpHandler();
        handler.RespondWith(HttpStatusCode.OK, CreateSwitchesJson());

        using var source = new WatchSwitchSource(CreateClient(handler), "test", TimeSpan.FromMilliseconds(50))
        {
            PollingEnabled = true
        };

        source.Start();
        await Task.Delay(200);
        source.Stop();

        // Wait a bit for the poll loop to notice cancellation
        await Task.Delay(100);

        var countAfterStop = handler.Requests.Count;
        await Task.Delay(200);

        // No new requests should be made after stop
        handler.Requests.Count.ShouldBe(countAfterStop);
    }

    [Fact]
    public void Stop_WithoutStart_DoesNotThrow()
    {
        var handler = new MockHttpHandler();
        var source = new WatchSwitchSource(CreateClient(handler), "test");

        source.Stop();
    }

    // --- DisposeAsync ---

#if NET10_0_OR_GREATER
    [Fact]
    public async Task DisposeAsync_StopsPolling()
    {
        var handler = new MockHttpHandler();
        handler.RespondWith(HttpStatusCode.OK, CreateSwitchesJson());

        var source = new WatchSwitchSource(CreateClient(handler), "test", TimeSpan.FromMilliseconds(50))
        {
            PollingEnabled = true
        };

        source.Start();
        await source.DisposeAsync();

        var countAfterDispose = handler.Requests.Count;
        await Task.Delay(200);

        handler.Requests.Count.ShouldBe(countAfterDispose);
    }

    [Fact]
    public async Task DisposeAsync_WithoutStart_DoesNotThrow()
    {
        var source = new WatchSwitchSource(CreateClient(new MockHttpHandler()), "test");

        await source.DisposeAsync();
    }
#endif

    // --- Domain encoding ---

    [Fact]
    public async Task UpdateAsync_EscapesDomainInUrl()
    {
        var handler = new MockHttpHandler();
        handler.RespondWith(HttpStatusCode.OK, CreateSwitchesJson());

        var source = new WatchSwitchSource(CreateClient(handler), "my domain/special");

        await source.UpdateAsync();

        handler.Requests[0].RequestUri.PathAndQuery.ShouldContain("domain=my%20domain%2Fspecial");
    }

    // --- Conditional GET (ETag / If-None-Match) ---

    // A server stub that answers conditional GETs from a mutable (etag, switches) pair: a matching
    // If-None-Match yields 304, otherwise 200 with the body and current ETag.
    private static void ServeConditional(MockHttpHandler handler, Func<string> etag, Func<SwitchDto[]> switches)
    {
        handler.SetHandler((request, _) =>
        {
            var current = etag();
            var inm = request.Headers.TryGetValues("If-None-Match", out var vals) ? vals.FirstOrDefault() : null;

            if (inm == current)
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.NotModified));

            var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = CreateSwitchesJson(switches()) };
            response.Headers.ETag = new System.Net.Http.Headers.EntityTagHeaderValue(current);
            return Task.FromResult(response);
        });
    }

    [Fact]
    public async Task UpdateAsync_FirstFetch_SendsNoIfNoneMatch()
    {
        var handler = new MockHttpHandler();
        ServeConditional(handler, () => "\"v1\"",
            () => [new SwitchDto { Pattern = "A", Level = (int)LogLevel.Debug }]);

        var source = new WatchSwitchSource(CreateClient(handler), "d");
        await source.UpdateAsync();

        handler.Requests[0].Headers.Contains("If-None-Match").ShouldBeFalse();
        source.Version.ShouldBe(1); // switches applied
    }

    [Fact]
    public async Task UpdateAsync_Unchanged_SendsETag_And_SkipsRebuild()
    {
        var handler = new MockHttpHandler();
        ServeConditional(handler, () => "\"v1\"",
            () => [new SwitchDto { Pattern = "A", Level = (int)LogLevel.Debug }]);

        var source = new WatchSwitchSource(CreateClient(handler), "d");

        await source.UpdateAsync();            // 200, applies switches, remembers "v1"
        var versionAfterFirst = source.Version;

        await source.UpdateAsync();            // sends If-None-Match "v1" -> 304

        handler.Requests[1].Headers.GetValues("If-None-Match").ShouldContain("\"v1\"");
        source.Version.ShouldBe(versionAfterFirst); // no rebuild on 304
    }

    [Fact]
    public async Task UpdateAsync_ChangedETag_Rebuilds()
    {
        var handler = new MockHttpHandler();
        var etag = "\"v1\"";
        var pattern = "A";
        ServeConditional(handler, () => etag,
            () => [new SwitchDto { Pattern = pattern, Level = (int)LogLevel.Debug }]);

        var source = new WatchSwitchSource(CreateClient(handler), "d");

        await source.UpdateAsync();            // applies v1
        var v1 = source.Version;

        // Server-side change: new etag + new switch set.
        etag = "\"v2\"";
        pattern = "B";

        await source.UpdateAsync();            // If-None-Match "v1" != "v2" -> 200, rebuild

        source.Version.ShouldBe(v1 + 1);
    }

}
