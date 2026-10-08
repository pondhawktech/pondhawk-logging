// Copyright (c) Pond Hawk Technologies Inc. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System.Text.Json;
using Amazon.CloudWatchLogs;
using InputLogEvent = Amazon.CloudWatchLogs.Model.InputLogEvent;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Pondhawk.Logging.CloudWatch.Tests.Support;
using Shouldly;
using Xunit;
using ZLogger;

namespace Pondhawk.Logging.CloudWatch.Tests;

/// <summary>
/// End-to-end tests: a Microsoft.Extensions.Logging factory whose CloudWatch provider delivers through a
/// <see cref="CloudWatchLoggerProcessor"/> to an in-memory CloudWatch Logs.
/// </summary>
public class CloudWatchLoggerProcessorTests
{
    private const string Group = "/pondhawk/Test-Group";

    private sealed class Harness : IDisposable
    {
        public FakeCloudWatchLogs Fake { get; init; }
        public CloudWatchLoggerProcessor Processor { get; init; }
        public CloudWatchDestination Destination { get; init; }
        public ILoggerFactory Factory { get; init; }
        public List<string> Notes { get; init; }
        public DateTime Now { get; set; } = DateTime.UtcNow;
        public int InstanceIdReads { get; set; }

        public ILogger Logger => Factory.CreateLogger("My.Category");

        /// <summary>Disposing the factory drains the processor, so everything logged has been dealt with.</summary>
        public void Dispose() => Factory.Dispose();
    }

    private static Harness Build(
        CloudWatchDestination destination = null,
        Action<FakeCloudWatchLogs> arrange = null,
        Action<CloudWatchOptions> configure = null,
        string instanceId = "i-0123",
        Func<IAmazonCloudWatchLogs> clientFactory = null,
        Func<string> instanceIdSource = null)
    {
        var (client, fake) = FakeCloudWatchLogs.Create();
        arrange?.Invoke(fake);

        // One event per batch, so each log call is its own send and the tests need not reason about batching.
        var options = new CloudWatchOptions { BatchSize = 1, FlushInterval = TimeSpan.FromMilliseconds(20) };
        configure?.Invoke(options);

        destination ??= new CloudWatchDestination(Group);
        var notes = new List<string>();

        Harness harness = null;

        var processor = new CloudWatchLoggerProcessor(
            clientFactory ?? (() => client),
            destination,
            "orders",
            options,
            () =>
            {
                harness.InstanceIdReads++;
                return instanceIdSource is not null ? instanceIdSource() : instanceId;
            },
            ownsClient: false,
            utcNow: () => harness.Now,
            note: message =>
            {
                lock (notes)
                    notes.Add(message);
            },
            heldRetryInterval: TimeSpan.FromMilliseconds(50));

        var factory = LoggerFactory.Create(b =>
        {
            b.SetMinimumLevel(LogLevel.Trace);
            // By factory, not by instance: the container disposes only what it built, and disposing the
            // provider is what drains the processor.
            b.Services.AddSingleton<ILoggerProvider>(_ => new CloudWatchLoggerProvider(processor, new ZLoggerOptions()));
        });

        harness = new Harness { Fake = fake, Processor = processor, Destination = destination, Factory = factory, Notes = notes };
        return harness;
    }

    private static async Task<bool> WaitUntil(Func<bool> condition)
    {
        for (var i = 0; i < 250; i++)
        {
            if (condition())
                return true;

            await Task.Delay(20);
        }

        return condition();
    }

    private static string Title(string message)
    {
        using var document = JsonDocument.Parse(message);
        return document.RootElement.GetProperty("Title").GetString();
    }

    // ── Where it writes ──

    [Fact]
    public void ItWritesToItsOwnStream_InTheGroup()
    {
        var harness = Build();

        harness.Logger.LogWarning("disk low");
        harness.Dispose();

        var stream = harness.Fake.Streams.ShouldHaveSingleItem();
        stream.LogGroupName.ShouldBe(Group);
        stream.LogStreamName.ShouldBe("i-0123/orders");

        var put = harness.Fake.Puts.ShouldHaveSingleItem();
        put.LogGroupName.ShouldBe(Group);
        put.LogStreamName.ShouldBe("i-0123/orders");
        harness.Processor.LogStream.ShouldBe("i-0123/orders");
        harness.Processor.IsReady.ShouldBeTrue();
    }

    [Fact]
    public void AStreamNameTheHostSupplies_IsUsed_AndInstanceMetadataIsNeverRead()
    {
        var harness = Build(configure: o => o.StreamName = "ecs/orders/task-1", instanceId: null);

        harness.Logger.LogWarning("disk low");
        harness.Dispose();

        harness.Fake.Puts.ShouldHaveSingleItem().LogStreamName.ShouldBe("ecs/orders/task-1");
        harness.InstanceIdReads.ShouldBe(0);
    }

    [Fact]
    public void EachEventIsOneJsonObject_FromTheLoggingApi()
    {
        var harness = Build();

        using (CorrelationManager.Begin("corr-42"))
        {
            CorrelationManager.SetSubject("kchen");
            CorrelationManager.SetTenant("acme");
            harness.Logger.ErrorWithContext(new InvalidOperationException("bad state"), new { OrderId = 42 }, "order failed");
        }

        harness.Logger.LogJson(LogLevel.Information, "the request", """{"Path":"/orders"}""");
        harness.Dispose();

        var messages = harness.Fake.Messages;
        messages.Count.ShouldBe(2);

        using var error = JsonDocument.Parse(messages[0]);
        error.RootElement.GetProperty("Level").GetString().ShouldBe("Error");
        error.RootElement.GetProperty("Category").GetString().ShouldBe("My.Category");
        error.RootElement.GetProperty("CorrelationId").GetString().ShouldBe("corr-42");
        error.RootElement.GetProperty("Subject").GetString().ShouldBe("kchen");
        error.RootElement.GetProperty("Tenant").GetString().ShouldBe("acme");
        error.RootElement.GetProperty("Title").GetString().ShouldBe("order failed");
        error.RootElement.GetProperty("Context").GetProperty("OrderId").GetInt32().ShouldBe(42);
        error.RootElement.GetProperty("Exceptions")[0].GetProperty("Message").GetString().ShouldBe("bad state");

        using var info = JsonDocument.Parse(messages[1]);
        info.RootElement.GetProperty("Payload").GetProperty("Path").GetString().ShouldBe("/orders");
        info.RootElement.TryGetProperty("CorrelationId", out _).ShouldBeFalse();
        info.RootElement.TryGetProperty("Subject", out _).ShouldBeFalse("set on the unit of work, which had ended");
    }

    // ── Start-up ──

    [Fact]
    public async Task BuildingIt_TouchesNoNetwork()
    {
        var built = 0;
        var (client, fake) = FakeCloudWatchLogs.Create();

        var harness = Build(clientFactory: () =>
        {
            built++;
            return client;
        });

        await Task.Delay(100);

        built.ShouldBe(0);
        fake.Calls.ShouldBe(0);
        harness.InstanceIdReads.ShouldBe(0);
        harness.Dispose();
    }

    [Fact]
    public void TheStreamIsMadeOnce_OnTheFirstBatch()
    {
        var harness = Build();

        harness.Logger.LogWarning("one");
        harness.Logger.LogWarning("two");
        harness.Logger.LogWarning("three");
        harness.Dispose();

        harness.Fake.Streams.Count.ShouldBe(1);
        harness.Fake.Messages.Select(Title).ShouldBe(["one", "two", "three"]);
        harness.InstanceIdReads.ShouldBe(1);
    }

    [Fact]
    public void ARestartedService_ReusesItsStream()
    {
        var harness = Build(arrange: f => f.StreamExists = true);

        harness.Logger.LogWarning("back again");
        harness.Dispose();

        harness.Fake.Puts.Count.ShouldBe(1);
        harness.Notes.ShouldBeEmpty();
    }

    [Fact]
    public void AClientThatCannotBeBuilt_TurnsCloudWatchOff_NotTheService()
    {
        var harness = Build(clientFactory: () => throw new InvalidOperationException("No RegionEndpoint or ServiceURL configured"));

        Should.NotThrow(() => harness.Logger.LogError("still running"));
        harness.Logger.LogError("and again");
        harness.Dispose();

        harness.Processor.IsOff.ShouldBeTrue();
        harness.Notes.ShouldHaveSingleItem().ShouldContain("could not build a CloudWatch Logs client");
    }

    [Fact]
    public async Task WithNoInstanceId_AndNoStreamName_CloudWatchTurnsOff_AfterAskingThreeTimes()
    {
        var harness = Build(instanceId: null);

        for (var attempt = 1; attempt <= CloudWatchLoggerProcessor.MaxInstanceIdAttempts; attempt++)
        {
            harness.Processor.IsOff.ShouldBeFalse();
            harness.Logger.LogError("attempt {Attempt}", attempt);
            (await WaitUntil(() => harness.InstanceIdReads == attempt)).ShouldBeTrue();
            (await WaitUntil(() => harness.Processor.IsPaused || harness.Processor.IsOff)).ShouldBeTrue();
            harness.Now += CloudWatchLoggerProcessor.PauseAfterFailure + TimeSpan.FromSeconds(1);
        }

        harness.Logger.LogError("after the verdict");
        harness.Dispose();

        harness.Fake.Calls.ShouldBe(0);
        harness.Processor.IsOff.ShouldBeTrue();
        harness.InstanceIdReads.ShouldBe(CloudWatchLoggerProcessor.MaxInstanceIdAttempts);
        harness.Notes.ShouldHaveSingleItem().ShouldContain("no instance id");
    }

    [Fact]
    public async Task AnInstanceId_ThatIsNotThereYet_IsAskedForAgain_AndNothingIsLost()
    {
        string instanceId = null;
        var harness = Build(instanceIdSource: () => instanceId);

        harness.Logger.LogWarning("metadata not up yet");
        (await WaitUntil(() => harness.Processor.IsPaused)).ShouldBeTrue();
        harness.Notes.ShouldBeEmpty();

        instanceId = "i-0456";
        harness.Now += CloudWatchLoggerProcessor.PauseAfterFailure + TimeSpan.FromSeconds(1);

        // No further event: the held warning goes out on its own once the pause is over.
        (await WaitUntil(() => harness.Fake.Messages.Count == 1)).ShouldBeTrue();
        harness.Dispose();

        harness.Fake.Puts.ShouldHaveSingleItem().LogStreamName.ShouldBe("i-0456/orders");
        harness.Fake.Messages.Select(Title).ShouldBe(["metadata not up yet"]);
    }

    // ── The group ──

    [Fact]
    public void AMissingGroup_IsCreated_WithRetention()
    {
        var harness = Build(arrange: f => f.GroupMissing = true);

        harness.Logger.LogWarning("first ever");
        harness.Dispose();

        harness.Fake.GroupsCreated.ShouldBe([Group]);
        var retention = harness.Fake.Retentions.ShouldHaveSingleItem();
        retention.LogGroupName.ShouldBe(Group);
        retention.RetentionInDays.ShouldBe(30);
        harness.Fake.Puts.Count.ShouldBe(1);
    }

    [Fact]
    public void AGroupAnotherHostCreated_KeepsItsOwnRetention()
    {
        var harness = Build(arrange: f =>
        {
            f.GroupMissing = true;
            f.GroupCreatedElsewhere = true;
        });

        harness.Logger.LogWarning("lost the race");
        harness.Dispose();

        harness.Fake.Retentions.ShouldBeEmpty();
        harness.Fake.Puts.Count.ShouldBe(1);
    }

    [Fact]
    public void AHostThatMayNotCreateTheGroup_TurnsCloudWatchOff_WithoutThrowing()
    {
        var harness = Build(arrange: f =>
        {
            f.GroupMissing = true;
            f.CreateGroupDenied = true;
        });

        harness.Logger.LogWarning("one");
        harness.Logger.LogWarning("two");
        harness.Dispose();

        harness.Fake.Puts.ShouldBeEmpty();
        harness.Fake.Streams.Count.ShouldBe(1);
        harness.Processor.IsOff.ShouldBeTrue();
        harness.Notes.ShouldHaveSingleItem().ShouldContain("logs:CreateLogGroup");
    }

    [Fact]
    public void RetentionThatCannotBeSet_DoesNotStopLogging()
    {
        var harness = Build(arrange: f =>
        {
            f.GroupMissing = true;
            f.RetentionFails = true;
        });

        harness.Logger.LogWarning("logged anyway");
        harness.Dispose();

        harness.Fake.Puts.Count.ShouldBe(1);
        harness.Notes.ShouldHaveSingleItem().ShouldContain("could not set its retention");
    }

    // ── Failure ──

    [Fact]
    public void NoPermission_TurnsCloudWatchOff_AndItStaysOffAfterThePause()
    {
        var harness = Build(arrange: f => f.Denied = true);

        harness.Logger.LogError("one");
        harness.Now += CloudWatchLoggerProcessor.PauseAfterFailure + TimeSpan.FromSeconds(1);
        harness.Logger.LogError("two");
        harness.Dispose();

        harness.Fake.Streams.Count.ShouldBe(1);
        harness.Fake.Puts.ShouldBeEmpty();
        harness.Processor.IsOff.ShouldBeTrue();
        harness.Processor.IsPaused.ShouldBeFalse();
        harness.Notes.Count.ShouldBe(1);
    }

    [Fact]
    public async Task AFailedPut_PausesCloudWatch_AndHoldsWarningsForWhenItIsBack()
    {
        var harness = Build(arrange: f => f.PutFails = true);

        harness.Logger.LogError("lost its send");
        (await WaitUntil(() => harness.Processor.IsPaused)).ShouldBeTrue();

        harness.Logger.LogInformation("routine, during the outage");
        harness.Logger.LogWarning("warned, during the outage");
        (await WaitUntil(() => harness.Processor.HeldEventCount == 2)).ShouldBeTrue();

        // One failed call, then CloudWatch is left alone: nothing more is attempted during the pause.
        harness.Fake.Calls.ShouldBe(2);   // the stream, and the one put
        harness.Notes.ShouldHaveSingleItem().ShouldContain("could not send a batch");

        harness.Fake.PutFails = false;
        harness.Now += CloudWatchLoggerProcessor.PauseAfterFailure + TimeSpan.FromSeconds(1);
        harness.Logger.LogInformation("back");
        harness.Dispose();

        // Warning and above survived the outage; the routine event did not.
        harness.Fake.Messages.Select(Title).ShouldBe(["lost its send", "warned, during the outage", "back"]);
        harness.Processor.HeldEventCount.ShouldBe(0);
        harness.Fake.Streams.Count.ShouldBe(1);
    }

    [Fact]
    public async Task AStreamThatCouldNotBeMade_IsTriedAgainAfterThePause()
    {
        var harness = Build(arrange: f => f.StreamOutages = 1);

        harness.Logger.LogWarning("during the outage");
        (await WaitUntil(() => harness.Processor.IsPaused)).ShouldBeTrue();
        harness.Processor.IsOff.ShouldBeFalse();

        harness.Now += CloudWatchLoggerProcessor.PauseAfterFailure + TimeSpan.FromSeconds(1);
        harness.Logger.LogWarning("after it");
        harness.Dispose();

        harness.Fake.Streams.Count.ShouldBe(2);
        harness.Fake.Messages.Select(Title).ShouldBe(["during the outage", "after it"]);
    }

    [Fact]
    public async Task AGroupDeletedUnderIt_IsMadeAgainAfterThePause()
    {
        var harness = Build();

        harness.Logger.LogWarning("before");
        (await WaitUntil(() => harness.Fake.Messages.Count == 1)).ShouldBeTrue();

        harness.Fake.GroupMissing = true;
        harness.Logger.LogWarning("group gone");
        (await WaitUntil(() => harness.Processor.IsPaused)).ShouldBeTrue();

        harness.Now += CloudWatchLoggerProcessor.PauseAfterFailure + TimeSpan.FromSeconds(1);
        harness.Logger.LogWarning("after");
        harness.Dispose();

        harness.Fake.GroupsCreated.ShouldBe([Group]);
        harness.Fake.Messages.Select(Title).ShouldBe(["before", "group gone", "after"]);
    }

    [Fact]
    public async Task HeldEvents_GoOutOnTheirOwn_OnceThePauseIsOver()
    {
        var harness = Build(arrange: f => f.PutFails = true);

        harness.Logger.LogError("the only thing this service logs today");
        (await WaitUntil(() => harness.Processor.HeldEventCount == 1)).ShouldBeTrue();

        harness.Fake.PutFails = false;
        harness.Now += CloudWatchLoggerProcessor.PauseAfterFailure + TimeSpan.FromSeconds(1);

        // Nothing else is logged; the held error must not wait for a next event that may never come.
        (await WaitUntil(() => harness.Fake.Messages.Count == 1)).ShouldBeTrue();
        harness.Processor.HeldEventCount.ShouldBe(0);
        harness.Dispose();
    }

    [Fact]
    public async Task HeldEvents_GetOneLastAttempt_AtShutdown_EvenDuringAPause()
    {
        var harness = Build(arrange: f => f.PutFails = true);

        harness.Logger.LogWarning("tripped the pause");
        (await WaitUntil(() => harness.Processor.IsPaused)).ShouldBeTrue();

        // CloudWatch is back, but the pause has most of a minute to run when the service exits.
        harness.Fake.PutFails = false;
        harness.Logger.LogCritical("the reason the service is exiting");
        harness.Dispose();

        harness.Fake.Messages.Select(Title).ShouldBe(["tripped the pause", "the reason the service is exiting"]);
    }

    [Fact]
    public async Task ABatchCloudWatchRefusesAsInvalid_IsDropped_AndDoesNotHoldUpTheRest()
    {
        var harness = Build(arrange: f => f.RejectMessagesContaining = "POISON");

        harness.Logger.LogWarning("before");
        harness.Logger.LogError("POISON");
        harness.Logger.LogWarning("after");
        harness.Logger.LogInformation("and routine events too");
        harness.Dispose();

        harness.Fake.Messages.Select(Title).ShouldBe(["before", "after", "and routine events too"]);
        harness.Processor.IsPaused.ShouldBeFalse();
        harness.Processor.HeldEventCount.ShouldBe(0);
        harness.Notes.ShouldHaveSingleItem().ShouldContain("refused a batch");
        await Task.CompletedTask;
    }

    [Fact]
    public async Task NoPermissionToPut_TurnsCloudWatchOff_NotedOnce()
    {
        var harness = Build(arrange: f => f.PutDenied = true);

        harness.Logger.LogError("one");
        (await WaitUntil(() => harness.Processor.IsOff)).ShouldBeTrue();

        harness.Now += CloudWatchLoggerProcessor.PauseAfterFailure + TimeSpan.FromSeconds(1);
        harness.Logger.LogError("two");
        harness.Dispose();

        harness.Processor.IsPaused.ShouldBeFalse();
        harness.Processor.HeldEventCount.ShouldBe(0);
        harness.Fake.Calls.ShouldBe(2);   // the stream, and the one refused put
        harness.Notes.ShouldHaveSingleItem().ShouldContain("logs:PutLogEvents");
    }

    [Fact]
    public void EventsCloudWatchRejects_AreNoted_NotSilent()
    {
        var harness = Build(arrange: f => f.RejectsSomeAsTooNew = true);

        harness.Logger.LogWarning("one");
        harness.Logger.LogWarning("two");
        harness.Dispose();

        // Said once, not per batch.
        harness.Notes.ShouldHaveSingleItem().ShouldContain("clock");
    }

    // ── Input that must not stop it ──

    private sealed class ThrowingMessageException : Exception
    {
        public override string Message => throw new NotSupportedException("the getter blew up");
    }

    [Fact]
    public void AnEventThatCannotBeFormatted_CostsNothingElseInItsBatch()
    {
        var harness = Build(configure: o =>
        {
            o.BatchSize = 10;
            o.FlushInterval = TimeSpan.FromMilliseconds(300);
        });

        harness.Logger.LogWarning("good one");
        harness.Logger.LogError(new ThrowingMessageException(), "the bad one");
        harness.Logger.LogWarning("good two");
        harness.Dispose();

        // All three arrive, the bad one included, with what could be said of its exception.
        harness.Fake.Messages.Select(Title).ShouldBe(["good one", "the bad one", "good two"]);
        harness.Fake.Messages[1].ShouldContain("Message threw NotSupportedException");
    }

    [Fact]
    public void AnOversizedEvent_IsCut_AndDeliveredWithTheOthers()
    {
        var harness = Build();

        harness.Logger.LogWarning("before");
        harness.Logger.LogError(new InvalidOperationException(new string('m', 2_000_000)), "{Body}", new string('t', 2_000_000));
        harness.Logger.LogText(LogLevel.Warning, "long first line", new string('<', 300_000) + "\nsecond line");
        harness.Logger.LogWarning("after");
        harness.Dispose();

        harness.Fake.Messages.Count.ShouldBe(4);
        harness.Fake.Messages.ShouldAllBe(m => System.Text.Encoding.UTF8.GetByteCount(m) <= CloudWatchEventFormatter.MaxEventBytes);
        Title(harness.Fake.Messages[3]).ShouldBe("after");
    }

    [Theory]
    [InlineData(0, 20)]
    [InlineData(-1, 20)]
    [InlineData(int.MaxValue, 20)]
    [InlineData(10, -5)]
    [InlineData(10, 0)]
    public void OptionValuesThatMakeNoSense_CannotStopDelivery(int batchSize, int flushMilliseconds)
    {
        var harness = Build(configure: o =>
        {
            o.BatchSize = batchSize;
            o.FlushInterval = TimeSpan.FromMilliseconds(flushMilliseconds);
            o.MaxHeldEvents = -3;
        });

        for (var i = 0; i < 5; i++)
            harness.Logger.LogWarning("warning {Number}", i);

        harness.Dispose();

        harness.Fake.Messages.Count.ShouldBe(5);
    }

    // ── Unbound and rebind ──

    [Fact]
    public async Task Unbound_CallsNothing_AndHoldsWarningsForTheRebind()
    {
        var harness = Build(CloudWatchDestination.Unbound());

        harness.Logger.LogInformation("starting");
        harness.Logger.LogWarning("came up degraded");
        harness.Logger.LogError("and then this");
        (await WaitUntil(() => harness.Processor.HeldEventCount == 2)).ShouldBeTrue();

        harness.Fake.Calls.ShouldBe(0);
        harness.InstanceIdReads.ShouldBe(0);
        harness.Notes.ShouldBeEmpty();
        harness.Processor.IsPaused.ShouldBeFalse();
        harness.Processor.IsOff.ShouldBeFalse();

        harness.Destination.Rebind(Group).ShouldBeTrue();
        harness.Logger.LogInformation("told where to log");
        harness.Dispose();

        harness.Fake.Puts.ShouldAllBe(p => p.LogGroupName == Group);
        harness.Fake.Messages.Select(Title).ShouldBe(["came up degraded", "and then this", "told where to log"]);
    }

    [Fact]
    public async Task WhileUnbound_OnlyTheMostRecentEventsAreHeld()
    {
        var harness = Build(CloudWatchDestination.Unbound(), configure: o => o.MaxHeldEvents = 3);

        for (var i = 1; i <= 5; i++)
            harness.Logger.LogWarning("warning {Number}", i);

        // Let the flush thread see all five while there is still nowhere to send them.
        (await WaitUntil(() => harness.Processor.HeldEventCount == 3)).ShouldBeTrue();
        await Task.Delay(200);

        harness.Destination.Rebind(Group);
        harness.Logger.LogInformation("bound");
        harness.Dispose();

        harness.Fake.Messages.Select(Title).Where(t => t.StartsWith("warning", StringComparison.Ordinal))
            .ShouldBe(["warning 3", "warning 4", "warning 5"]);
    }

    [Fact]
    public async Task ARebind_MakesTheStreamInTheNewGroup_AndWritesThere()
    {
        var harness = Build();

        harness.Logger.LogWarning("to the first");
        (await WaitUntil(() => harness.Fake.Messages.Count == 1)).ShouldBeTrue();

        harness.Destination.Rebind("/pondhawk/Other-Group").ShouldBeTrue();
        harness.Logger.LogWarning("to the second");
        harness.Dispose();

        harness.Fake.Streams.Select(s => s.LogGroupName).ShouldBe([Group, "/pondhawk/Other-Group"]);
        harness.Fake.Puts.Select(p => p.LogGroupName).ShouldBe([Group, "/pondhawk/Other-Group"]);
        harness.InstanceIdReads.ShouldBe(1);
    }

    [Fact]
    public async Task AGroupThatWasDenied_DoesNotHoldTheNextOneShut()
    {
        var harness = Build(arrange: f => f.DeniedGroup = Group);

        harness.Logger.LogWarning("denied");
        (await WaitUntil(() => harness.Processor.IsOff)).ShouldBeTrue();

        harness.Destination.Rebind("/pondhawk/Allowed-Group");
        harness.Logger.LogWarning("allowed");
        harness.Dispose();

        harness.Processor.IsReady.ShouldBeTrue();
        harness.Fake.Messages.Select(Title).ShouldBe(["allowed"]);
    }

    // ── Chunking ──

    private static CloudWatchLoggerProcessor.Outgoing Outgoing(string message, DateTime? at = null)
        => new(new InputLogEvent { Message = message, Timestamp = at ?? DateTime.UtcNow }, Hold: false);

    [Fact]
    public void ABatchIsSplit_AtTheByteLimit_Counting26BytesAnEvent()
    {
        // Each event counts 1,000 + 26 bytes: 1,022 fit in 1,048,576, the 1,023rd does not.
        var events = Enumerable.Range(0, 1_023).Select(_ => Outgoing(new string('x', 1_000))).ToList();

        var chunks = CloudWatchLoggerProcessor.Chunk(events).ToList();

        chunks.Select(c => c.Count).ShouldBe([1_022, 1]);
    }

    [Fact]
    public void ABatchIsSplit_AtTenThousandEvents()
    {
        var events = Enumerable.Range(0, 10_001).Select(_ => Outgoing("x")).ToList();

        CloudWatchLoggerProcessor.Chunk(events).Select(c => c.Count).ShouldBe([10_000, 1]);
    }

    [Fact]
    public void ABatchIsSplit_WhenItWouldSpanADay()
    {
        var start = new DateTime(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);
        var events = new[] { Outgoing("a", start), Outgoing("b", start.AddHours(23)), Outgoing("c", start.AddHours(24)) };

        CloudWatchLoggerProcessor.Chunk(events).Select(c => c.Count).ShouldBe([2, 1]);
    }
}
