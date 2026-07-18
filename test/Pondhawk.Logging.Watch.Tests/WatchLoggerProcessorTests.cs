// Copyright (c) Pond Hawk Technologies Inc. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System.Diagnostics;
using System.Drawing;
using System.Net;
using Microsoft.Extensions.Logging;
using Pondhawk.Logging;
using Pondhawk.Logging.Watch.Tests.Http;
using Shouldly;
using Xunit;
using ZLogger;

namespace Pondhawk.Logging.Watch.Tests;

/// <summary>
/// End-to-end tests: a Microsoft.Extensions.Logging factory whose ZLogger provider delivers through a
/// <see cref="WatchLoggerProcessor"/>, asserting the MemoryPack batches posted to the (mock) Watch server.
/// </summary>
public class WatchLoggerProcessorTests
{
    private static HttpClient CreateClient(MockHttpHandler handler)
        => new(handler) { BaseAddress = new Uri("http://localhost/") };

    private static (ILoggerFactory Factory, List<LogEvent> Delivered) Build(MockHttpHandler handler, SwitchSource switches)
    {
        var delivered = new List<LogEvent>();

        handler.SetHandler(async (req, ct) =>
        {
            var stream = await req.Content.ReadAsStreamAsync(ct);
            var batch = await LogEventBatchSerializer.FromStream(stream);
            if (batch is not null)
            {
                lock (delivered)
                    delivered.AddRange(batch.Events);
            }

            return new HttpResponseMessage(HttpStatusCode.OK);
        });

        var processor = new WatchLoggerProcessor(
            CreateClient(handler), switches, "TestDomain", batchSize: 1, flushInterval: TimeSpan.FromMilliseconds(20));

        var factory = LoggerFactory.Create(b =>
        {
            b.SetMinimumLevel(LogLevel.Trace);
            b.AddZLoggerLogProcessor((_, _) => processor);
        });

        return (factory, delivered);
    }

    private static async Task<LogEvent> WaitForFirst(List<LogEvent> delivered)
    {
        for (var i = 0; i < 100; i++)
        {
            lock (delivered)
            {
                if (delivered.Count > 0)
                    return delivered[0];
            }

            await Task.Delay(20);
        }

        lock (delivered)
            return delivered.FirstOrDefault();
    }

    [Fact]
    public async Task Delivers_LogEvent_WithCategoryTitleAndLevel()
    {
        var handler = new MockHttpHandler();
        var (factory, delivered) = Build(handler, new SwitchSource());

        factory.CreateLogger("My.Category").LogInformation("hello");

        var e = await WaitForFirst(delivered);
        e.ShouldNotBeNull();
        e.Category.ShouldBe("My.Category");
        e.Title.ShouldBe("hello");
        e.Level.ShouldBe((int)LogLevel.Information);
    }

    [Fact]
    public async Task Applies_SwitchColorAndTag()
    {
        var handler = new MockHttpHandler();
        var switches = new SwitchSource();
        switches.WhenMatched("My", "MyTag", LogLevel.Trace, Color.Red);
        var (factory, delivered) = Build(handler, switches);

        factory.CreateLogger("My.Service").LogWarning("warn");

        var e = await WaitForFirst(delivered);
        e.ShouldNotBeNull();
        e.Color.ShouldBe(Color.Red.ToArgb());
        e.Tag.ShouldBe("MyTag");
    }

    [Fact]
    public async Task Captures_CorrelationId_FromActivityBaggage()
    {
        var handler = new MockHttpHandler();
        var (factory, delivered) = Build(handler, new SwitchSource());

        using var activity = new Activity("test");
        activity.Start();
        activity.SetBaggage(LogPropertyNames.CorrelationBaggageKey, "CID-123");

        factory.CreateLogger("C").LogInformation("x");

        var e = await WaitForFirst(delivered);
        e.ShouldNotBeNull();
        e.CorrelationId.ShouldBe("CID-123");

        activity.Stop();
    }

    [Fact]
    public async Task Delivers_LogObject_AsJsonPayload()
    {
        var handler = new MockHttpHandler();
        var (factory, delivered) = Build(handler, new SwitchSource());

        factory.CreateLogger("P").LogObject("the-widget", new { Name = "W" });

        var e = await WaitForFirst(delivered);
        e.ShouldNotBeNull();
        e.Title.ShouldBe("the-widget");
        e.Type.ShouldBe((int)PayloadType.Json);
        e.Payload.ShouldContain("W");
    }
}
