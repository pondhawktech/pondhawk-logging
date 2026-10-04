// Copyright (c) Pond Hawk Technologies Inc. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Pondhawk.Logging.CloudWatch.Tests.Support;
using Shouldly;
using Xunit;
using ZLogger;

namespace Pondhawk.Logging.CloudWatch.Tests;

/// <summary>
/// Tests the <c>AddCloudWatch</c> wiring: the fixed level floor scoped to the CloudWatch provider, and the
/// destination's registration.
/// </summary>
public class AddCloudWatchTests
{
    private sealed class CountingProcessor : IAsyncLogProcessor
    {
        public List<LogLevel> Levels { get; } = [];

        public void Post(IZLoggerEntry log)
        {
            lock (Levels)
                Levels.Add(log.LogInfo.LogLevel);
            log.Return();
        }

        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }

    private static List<string> Levels(FakeCloudWatchLogs fake) =>
        fake.Messages.Select(m =>
        {
            using var document = JsonDocument.Parse(m);
            return document.RootElement.GetProperty("Level").GetString();
        }).ToList();

    private static void LogEveryLevel(ILogger logger)
    {
        logger.LogTrace("trace");
        logger.LogDebug("debug");
        logger.LogInformation("information");
        logger.LogWarning("warning");
        logger.LogError("error");
        logger.LogCritical("critical");
    }

    private static ILoggingBuilder AddFake(ILoggingBuilder builder, Amazon.CloudWatchLogs.IAmazonCloudWatchLogs client)
    {
        var options = new CloudWatchOptions { FlushInterval = TimeSpan.FromMilliseconds(20) };

        return builder.AddCloudWatch(() => client, new CloudWatchDestination("/pondhawk/Test-Group"), "orders", options, () => "i-0123", ownsClient: false);
    }

    [Fact]
    public void InformationAndAbove_IsSent_DebugAndTraceNever()
    {
        var (client, fake) = FakeCloudWatchLogs.Create();
        var factory = LoggerFactory.Create(b => AddFake(b, client));

        LogEveryLevel(factory.CreateLogger("My.Category"));
        factory.Dispose();

        Levels(fake).ShouldBe(["Information", "Warning", "Error", "Critical"]);
    }

    [Fact]
    public void TheFloor_NeitherFollowsNorClamps_AnotherProvidersGate()
    {
        // What AddWatch does: opens the floor and gates every category with a global filter (here: Error
        // only), delivering through another ZLogger processor.
        var (client, fake) = FakeCloudWatchLogs.Create();
        var other = new CountingProcessor();

        var factory = LoggerFactory.Create(b =>
        {
            b.SetMinimumLevel(LogLevel.Trace);
            b.AddFilter((_, level) => level is LogLevel.Trace or >= LogLevel.Error);
            b.AddZLoggerLogProcessor(other);
            AddFake(b, client);
        });

        LogEveryLevel(factory.CreateLogger("My.Category"));
        factory.Dispose();

        // CloudWatch keeps its own floor whatever the global gate says ...
        Levels(fake).ShouldBe(["Information", "Warning", "Error", "Critical"]);

        // ... and that floor does not reach the other processor, which still follows the global gate.
        other.Levels.ShouldBe([LogLevel.Trace, LogLevel.Error, LogLevel.Critical]);
    }

    [Fact]
    public void AddCloudWatch_RegistersTheDestination_AndCannotStopTheHostStarting()
    {
        var destination = CloudWatchDestination.Unbound();

        var services = new ServiceCollection();
        services.AddLogging(b => b.AddCloudWatch(destination, "orders"));

        using var provider = services.BuildServiceProvider();

        provider.GetRequiredService<CloudWatchDestination>().ShouldBeSameAs(destination);

        // Unbound, so this reaches the real processor and no AWS client is ever built.
        Should.NotThrow(() => provider.GetRequiredService<ILoggerFactory>().CreateLogger("My.Category").LogError("no group yet"));
    }

    [Fact]
    public void AddCloudWatch_RejectsAGroupNameCloudWatchWouldRefuse()
    {
        var services = new ServiceCollection();

        Should.Throw<ArgumentException>(() => services.AddLogging(b => b.AddCloudWatch("not a valid:group", "orders")));
    }
}
