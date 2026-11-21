// Copyright (c) Pond Hawk Technologies Inc. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Shouldly;
using Xunit;

namespace Pondhawk.Logging.Watch.Tests.Http;

/// <summary>
/// Long polling for switches (<c>Prefer: wait</c>): when the next poll is sent, and what ends a held one.
/// </summary>
public class WatchSwitchSourceLongPollTests
{
    private static readonly TimeSpan Interval = TimeSpan.FromSeconds(1);

    private static HttpResponseMessage Switches(string etag, bool held)
    {
        var json = JsonSerializer.Serialize(new SwitchesResponse
        {
            Switches = [new SwitchDto { Pattern = "A", Level = (int)LogLevel.Debug }]
        });
        var response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
        response.Headers.ETag = new EntityTagHeaderValue(etag);
        if (held)
            response.Headers.TryAddWithoutValidation("Preference-Applied", "wait=55");
        return response;
    }

    private static HttpResponseMessage NotModified(bool held)
    {
        var response = new HttpResponseMessage(HttpStatusCode.NotModified);
        if (held)
            response.Headers.TryAddWithoutValidation("Preference-Applied", "wait=55");
        return response;
    }

    private static string PreferOf(HttpRequestMessage request)
        => request.Headers.TryGetValues("Prefer", out var values) ? values.Single() : null;

    // A Watch that answers the first poll with switches and every conditional one with a 304 after a short hold.
    private static (MockHttpHandler Handler, ConcurrentQueue<HttpRequestMessage> Requests) Watch(bool held)
    {
        var requests = new ConcurrentQueue<HttpRequestMessage>();
        var handler = new MockHttpHandler();
        handler.SetHandler(async (request, ct) =>
        {
            requests.Enqueue(request);
            if (!request.Headers.Contains("If-None-Match"))
                return Switches("\"v1\"", held);

            await Task.Delay(20, ct);
            return NotModified(held);
        });
        return (handler, requests);
    }

    private static async Task Until(Func<bool> condition, TimeSpan within)
    {
        var clock = Stopwatch.StartNew();
        while (!condition())
        {
            if (clock.Elapsed > within)
                throw new TimeoutException("The condition was not met in time.");
            await Task.Delay(10);
        }
    }

    [Fact]
    public async Task AfterAHeldAnswer_PollsAgainAtOnce_AskingWatchToWait()
    {
        var (handler, requests) = Watch(held: true);
        using var source = new WatchSwitchSource(new HttpClient(handler) { BaseAddress = new Uri("http://watch.example") }, "d", Interval);

        source.Start();

        // Back to back: every answer carries Preference-Applied.
        await Until(() => requests.Count >= 10, TimeSpan.FromSeconds(5));

        var polls = requests.ToArray();
        PreferOf(polls[0]).ShouldBeNull();          // no ETag yet, so nothing to hold on
        PreferOf(polls[^1]).ShouldBe("wait=55");
    }

    [Fact]
    public async Task WithoutPreferenceApplied_WaitsThePollInterval()
    {
        var (handler, requests) = Watch(held: false);
        using var source = new WatchSwitchSource(new HttpClient(handler) { BaseAddress = new Uri("http://watch.example") }, "d", Interval);

        source.Start();
        await Task.Delay(Interval * 1.5);

        // An older Watch: the first poll, then one per interval.
        requests.Count.ShouldBeLessThanOrEqualTo(3);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AfterAFailure_WaitsThePollInterval(bool throws)
    {
        var requests = new ConcurrentQueue<HttpRequestMessage>();
        var handler = new MockHttpHandler();
        handler.SetHandler((request, _) =>
        {
            requests.Enqueue(request);
            if (!request.Headers.Contains("If-None-Match"))
                return Task.FromResult(Switches("\"v1\"", held: true));

            // A proxy cutting the held request short, with the header Watch would have sent.
            if (throws)
                throw new HttpRequestException("connection reset");
            var bad = new HttpResponseMessage(HttpStatusCode.BadGateway);
            bad.Headers.TryAddWithoutValidation("Preference-Applied", "wait=55");
            return Task.FromResult(bad);
        });
        using var source = new WatchSwitchSource(new HttpClient(handler) { BaseAddress = new Uri("http://watch.example") }, "d", Interval);

        source.Start();
        await Task.Delay(Interval * 1.5);

        // The first poll, the immediate held one that fails, then at most one more: never a tight loop.
        requests.Count.ShouldBeLessThanOrEqualTo(3);
    }

    [Fact]
    public async Task RebindDuringAHeldPoll_CancelsIt_AndPollsTheNewDomainAtOnce()
    {
        var held = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        var second = new TaskCompletionSource<HttpRequestMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new MockHttpHandler();
        handler.SetHandler(async (request, ct) =>
        {
            if (request.RequestUri!.Host == "second.example")
            {
                second.TrySetResult(request);
                return Switches("\"s1\"", held: false);
            }

            if (!request.Headers.Contains("If-None-Match"))
                return Switches("\"v1\"", held: true);

            held.TrySetResult(ct);
            await Task.Delay(Timeout.Infinite, ct);   // held until cancelled
            return NotModified(held: true);
        });

        var destination = new WatchDestination("http://first.example/d1");
        using var source = new WatchSwitchSource(new HttpClient(handler), destination, TimeSpan.FromMinutes(10));

        source.Start();
        var heldToken = await held.Task.WaitAsync(TimeSpan.FromSeconds(5));

        destination.Rebind("http://second.example/d2");

        var request = await second.Task.WaitAsync(TimeSpan.FromSeconds(2));
        request.RequestUri.ShouldBe(new Uri("http://second.example/api/switches?domain=d2"));
        request.Headers.Contains("If-None-Match").ShouldBeFalse();
        heldToken.IsCancellationRequested.ShouldBeTrue();
    }

    [Fact]
    public async Task RebindWhileWaitingTheInterval_PollsTheNewDomainAtOnce()
    {
        var second = new TaskCompletionSource<HttpRequestMessage>(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new MockHttpHandler();
        handler.SetHandler((request, _) =>
        {
            if (request.RequestUri!.Host == "second.example")
                second.TrySetResult(request);
            return Task.FromResult(Switches("\"v1\"", held: false));
        });

        var destination = WatchDestination.Unbound();
        using var source = new WatchSwitchSource(new HttpClient(handler), destination, TimeSpan.FromMinutes(10));

        source.Start();
        destination.Rebind("http://second.example/d2");

        await second.Task.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task DisposeDuringAHeldPoll_ReturnsPromptly()
    {
        var held = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var handler = new MockHttpHandler();
        handler.SetHandler(async (request, ct) =>
        {
            if (!request.Headers.Contains("If-None-Match"))
                return Switches("\"v1\"", held: true);

            held.TrySetResult();
            await Task.Delay(Timeout.Infinite, ct);
            return NotModified(held: true);
        });

        var source = new WatchSwitchSource(new HttpClient(handler) { BaseAddress = new Uri("http://watch.example") }, "d", TimeSpan.FromMinutes(10));
        source.Start();
        await held.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await source.DisposeAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public async Task LongPollWaitZero_SendsNoPrefer()
    {
        var (handler, requests) = Watch(held: false);
        using var source = new WatchSwitchSource(new HttpClient(handler) { BaseAddress = new Uri("http://watch.example") }, "d", TimeSpan.FromMilliseconds(20))
        {
            LongPollWait = TimeSpan.Zero
        };

        source.Start();
        await Until(() => requests.Count >= 3, TimeSpan.FromSeconds(5));

        requests.ShouldAllBe(r => !r.Headers.Contains("Prefer"));
        requests.Skip(1).ShouldAllBe(r => r.Headers.Contains("If-None-Match"));
    }

    [Fact]
    public async Task UpdateAsync_IsNeverHeld()
    {
        var (handler, requests) = Watch(held: true);
        var source = new WatchSwitchSource(new HttpClient(handler) { BaseAddress = new Uri("http://watch.example") }, "d");

        await source.UpdateAsync();
        await source.UpdateAsync();

        requests.Last().Headers.Contains("If-None-Match").ShouldBeTrue();
        requests.Last().Headers.Contains("Prefer").ShouldBeFalse();
    }

    [Fact]
    public async Task AShortClientTimeout_LowersTheWait()
    {
        var (handler, requests) = Watch(held: true);
        var client = new HttpClient(handler) { BaseAddress = new Uri("http://watch.example"), Timeout = TimeSpan.FromSeconds(30) };
        using var source = new WatchSwitchSource(client, "d", TimeSpan.FromMilliseconds(20));

        source.Start();
        await Until(() => requests.Count >= 2, TimeSpan.FromSeconds(5));

        PreferOf(requests.ElementAt(1)).ShouldBe("wait=15");
    }

    [Theory]
    [InlineData(55, 100, 55)]       // HttpClient's default timeout leaves room
    [InlineData(55, 30, 15)]        // lowered to fit, with the grace
    [InlineData(55, 16, 1)]
    [InlineData(55, 15, 0)]         // too short: no Prefer at all
    [InlineData(0, 100, 0)]         // long polling off
    [InlineData(-5, 100, 0)]
    [InlineData(10.9, 100, 10)]     // whole seconds
    public void EffectiveWait_FitsTheWaitInsideTheClientTimeout(double configured, double timeout, int expected)
    {
        WatchSwitchSource.EffectiveWait(TimeSpan.FromSeconds(configured), TimeSpan.FromSeconds(timeout)).ShouldBe(expected);
    }

    [Fact]
    public void EffectiveWait_WithNoClientTimeout_KeepsTheWait()
    {
        WatchSwitchSource.EffectiveWait(TimeSpan.FromSeconds(55), Timeout.InfiniteTimeSpan).ShouldBe(55);
    }

    [Fact]
    public void LongPollWait_DefaultsTo55Seconds()
    {
        new WatchSwitchSource(new HttpClient(new MockHttpHandler()), "d").LongPollWait.ShouldBe(TimeSpan.FromSeconds(55));
        new WatchOptions().LongPollWait.ShouldBeNull();
    }
}
