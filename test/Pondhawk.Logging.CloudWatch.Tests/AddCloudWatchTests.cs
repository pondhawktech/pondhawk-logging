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
/// Tests the <c>AddCloudWatch</c> wiring: the fixed level floor scoped to the CloudWatch provider, the
/// destination's registration, and routing categories between registrations.
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
    public void DebugAndTrace_NeverGo_EvenWhenAMoreSpecificRuleAsksForThem()
    {
        // A rule naming the provider and a category outranks the floor AddCloudWatch registers; an
        // appsettings "Logging:CloudWatch:LogLevel:My" entry is the same thing.
        var (client, fake) = FakeCloudWatchLogs.Create();
        var factory = LoggerFactory.Create(b =>
        {
            AddFake(b, client);
            b.AddFilter<CloudWatchLoggerProvider>("My", LogLevel.Trace);
        });

        LogEveryLevel(factory.CreateLogger("My.Category"));
        factory.Dispose();

        Levels(fake).ShouldBe(["Information", "Warning", "Error", "Critical"]);
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

    // Routing by category

    private const string MainGroup = "/pondhawk/Main";
    private const string AccessGroup = "/pondhawk/Access";
    private const string Access = "Pondhawk.Gateway.Access";

    private static ILoggingBuilder AddFake(ILoggingBuilder builder, Amazon.CloudWatchLogs.IAmazonCloudWatchLogs client, string group, Action<CloudWatchOptions> route)
    {
        var options = new CloudWatchOptions { FlushInterval = TimeSpan.FromMilliseconds(20) };
        route(options);

        return builder.AddCloudWatch(() => client, new CloudWatchDestination(group), "gateway", options, () => "i-0123", ownsClient: false);
    }

    // Read once the factory is disposed, when every batch has been sent.
    private static List<string> Categories(FakeCloudWatchLogs fake, string group) =>
        fake.Puts.Where(p => p.LogGroupName == group).SelectMany(p => p.LogEvents).Select(e =>
        {
            using var document = JsonDocument.Parse(e.Message);
            return document.RootElement.GetProperty("Category").GetString();
        }).ToList();

    private static ILoggerFactory MainAndAccess(Amazon.CloudWatchLogs.IAmazonCloudWatchLogs client) =>
        LoggerFactory.Create(b =>
        {
            AddFake(b, client, MainGroup, o => o.ExceptCategories(Access));
            AddFake(b, client, AccessGroup, o => o.OnlyCategories(Access));
        });

    [Fact]
    public void EachCategory_GoesToExactlyOneOfTwoRoutedRegistrations()
    {
        var (client, fake) = FakeCloudWatchLogs.Create();
        var factory = MainAndAccess(client);

        foreach (var category in new[] { Access, Access + ".Bff", "pondhawk.gateway.access", Access + "X", "Pondhawk.Gateway", "Orders" })
            factory.CreateLogger(category).LogInformation("one");
        factory.Dispose();

        Categories(fake, AccessGroup).ShouldBe([Access, Access + ".Bff", "pondhawk.gateway.access"], ignoreOrder: true);
        Categories(fake, MainGroup).ShouldBe([Access + "X", "Pondhawk.Gateway", "Orders"], ignoreOrder: true);
    }

    [Fact]
    public void ACategoryARegistrationDoesNotSend_IsNeverEnabledForIt()
    {
        var (client, _) = FakeCloudWatchLogs.Create();

        using (var only = LoggerFactory.Create(b => AddFake(b, client, AccessGroup, o => o.OnlyCategories(Access))))
        {
            only.CreateLogger("Orders").IsEnabled(LogLevel.Critical).ShouldBeFalse();
            only.CreateLogger(Access + "X").IsEnabled(LogLevel.Critical).ShouldBeFalse();
            only.CreateLogger(Access + ".Bff").IsEnabled(LogLevel.Information).ShouldBeTrue();
        }

        using (var except = LoggerFactory.Create(b => AddFake(b, client, MainGroup, o => o.ExceptCategories(Access))))
        {
            except.CreateLogger(Access).IsEnabled(LogLevel.Critical).ShouldBeFalse();
            except.CreateLogger(Access + ".Bff").IsEnabled(LogLevel.Critical).ShouldBeFalse();
            except.CreateLogger("Orders").IsEnabled(LogLevel.Information).ShouldBeTrue();
        }
    }

    [Fact]
    public void AnExcludedCategory_IsNeverFormatted()
    {
        var (client, _) = FakeCloudWatchLogs.Create();
        var (excluded, included) = (false, false);

        var factory = LoggerFactory.Create(b => AddFake(b, client, MainGroup, o => o.ExceptCategories(Access)));
        factory.CreateLogger(Access).LogError("{Value}", new Formatted(() => excluded = true));
        factory.CreateLogger("Orders").LogError("{Value}", new Formatted(() => included = true));
        factory.Dispose();

        included.ShouldBeTrue();
        excluded.ShouldBeFalse();
    }

    private sealed class Formatted(Action onFormat)
    {
        public override string ToString()
        {
            onFormat();
            return "formatted";
        }
    }

    [Fact]
    public void OneGroupDenied_LeavesTheOtherSending()
    {
        var (client, fake) = FakeCloudWatchLogs.Create();
        fake.DeniedGroup = AccessGroup;
        var factory = MainAndAccess(client);

        for (var i = 0; i < 3; i++)
        {
            factory.CreateLogger(Access).LogWarning("access {I}", i);
            factory.CreateLogger("Orders").LogWarning("main {I}", i);
            Thread.Sleep(50);
        }

        factory.Dispose();

        Categories(fake, AccessGroup).ShouldBeEmpty();
        Categories(fake, MainGroup).ShouldBe(["Orders", "Orders", "Orders"]);
    }

    [Fact]
    public void WithNoRouting_EveryCategoryIsSent()
    {
        new CloudWatchOptions().Categories.Includes("Anything.At.All").ShouldBeTrue();
    }

    [Fact]
    public void OnlyAndExcept_CannotBothBeSet()
    {
        Should.Throw<InvalidOperationException>(() => new CloudWatchOptions().OnlyCategories("A").ExceptCategories("B"));
        Should.Throw<InvalidOperationException>(() => new CloudWatchOptions().ExceptCategories("A").OnlyCategories("B"));
    }

    [Fact]
    public void OnlyAndExcept_TakeSeveralNames_AcrossCalls()
    {
        var route = new CloudWatchOptions().OnlyCategories("A", "B").OnlyCategories("C").Categories;

        route.Includes("A.X").ShouldBeTrue();
        route.Includes("B").ShouldBeTrue();
        route.Includes("C").ShouldBeTrue();
        route.Includes("D").ShouldBeFalse();
    }

    [Theory]
    [InlineData("")]
    [InlineData("  ")]
    public void ABlankCategory_IsRefused(string category)
    {
        Should.Throw<ArgumentException>(() => new CloudWatchOptions().OnlyCategories(category));
    }
}
