// Copyright (c) Pond Hawk Technologies Inc. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System.Text.Json;
using Amazon;
using Amazon.CloudWatchLogs;
using Amazon.CloudWatchLogs.Model;
using Amazon.Runtime;
using Amazon.Runtime.CredentialManagement;
using Microsoft.Extensions.Logging;
using Shouldly;
using Xunit;

namespace Pondhawk.Logging.CloudWatch.Tests;

/// <summary>
/// Runs only when <c>CLOUDWATCH_LIVE_PROFILE</c> names a local AWS profile, so a plain <c>dotnet test</c>
/// and CI skip it: <c>CLOUDWATCH_LIVE_PROFILE=my-profile dotnet test --filter Category=Aws</c>.
/// </summary>
public sealed class LiveFactAttribute : FactAttribute
{
    public const string ProfileVariable = "CLOUDWATCH_LIVE_PROFILE";

    public LiveFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(ProfileVariable)))
            Skip = $"Set {ProfileVariable} to a local AWS profile to run against the real CloudWatch Logs.";
    }
}

/// <summary>
/// The provider against the real service, through the public <c>AddCloudWatch</c>: stream created, events
/// put, and the JSON read back as Logs Insights would see it. Each test makes its own throwaway log group
/// and deletes it afterwards.
/// </summary>
[Trait("Category", "Aws")]
public class CloudWatchLiveTests
{
    private static (AWSCredentials Credentials, RegionEndpoint Region) Profile()
    {
        var name = Environment.GetEnvironmentVariable(LiveFactAttribute.ProfileVariable);
        var chain = new CredentialProfileStoreChain();

        chain.TryGetProfile(name, out var profile).ShouldBeTrue($"no local AWS profile named {name}");
        chain.TryGetAWSCredentials(name, out var credentials).ShouldBeTrue($"no credentials in the local AWS profile {name}");
        profile.Region.ShouldNotBeNull($"the local AWS profile {name} names no region");

        return (credentials, profile.Region);
    }

    private static async Task<List<JsonElement>> Read(IAmazonCloudWatchLogs client, string group, string stream, int expected)
    {
        // PutLogEvents is accepted before the events are readable.
        GetLogEventsResponse read = null;
        for (var i = 0; i < 30 && (read?.Events?.Count ?? 0) < expected; i++)
        {
            await Task.Delay(1000);
            read = await client.GetLogEventsAsync(new GetLogEventsRequest { LogGroupName = group, LogStreamName = stream, StartFromHead = true });
        }

        return (read?.Events ?? []).Select(e =>
        {
            using var document = JsonDocument.Parse(e.Message);
            return document.RootElement.Clone();
        }).ToList();
    }

    private static async Task Delete(IAmazonCloudWatchLogs client, string group)
    {
        try
        {
            await client.DeleteLogGroupAsync(new DeleteLogGroupRequest { LogGroupName = group });
        }
        catch (ResourceNotFoundException)
        {
            // The test failed before the group existed.
        }
    }

    [LiveFact]
    public async Task EventsArriveAsJson_InAStreamOfAGroupTheProviderCreated_WithRetention()
    {
        var (credentials, region) = Profile();
        var group = $"/pondhawk-logging/test-{Guid.NewGuid():N}";
        const string Stream = "live/orders";

        using var client = new AmazonCloudWatchLogsClient(credentials, region);

        try
        {
            var factory = LoggerFactory.Create(b => b.AddCloudWatch(group, "orders", o =>
            {
                o.Credentials = credentials;
                o.Region = region;
                o.StreamName = Stream;
            }));

            var logger = factory.CreateLogger("Pondhawk.Logging.Live");

            logger.LogDebug("never sent");
            logger.LogInformation("first event of a new group");
            using (CorrelationManager.Begin("01KLIVE"))
                logger.ErrorWithContext(Thrown(), new { OrderId = 42 }, "live test");

            factory.Dispose();

            var events = await Read(client, group, Stream, expected: 2);

            events.Select(e => e.GetProperty("Title").GetString()).ShouldBe(["first event of a new group", "live test"]);

            var error = events[1];
            error.GetProperty("Level").GetString().ShouldBe("Error");
            error.GetProperty("Category").GetString().ShouldBe("Pondhawk.Logging.Live");
            error.GetProperty("CorrelationId").GetString().ShouldBe("01KLIVE");
            error.GetProperty("Context").GetProperty("OrderId").GetInt32().ShouldBe(42);
            error.GetProperty("Exceptions")[0].GetProperty("Message").GetString().ShouldBe("live");

            var groups = await client.DescribeLogGroupsAsync(new DescribeLogGroupsRequest { LogGroupNamePrefix = group });
            groups.LogGroups.Single(g => string.Equals(g.LogGroupName, group, StringComparison.Ordinal)).RetentionInDays.ShouldBe(30);
        }
        finally
        {
            await Delete(client, group);
        }
    }

    [LiveFact]
    public async Task AnExistingGroup_KeepsItsRetention_AndARestartReusesTheStream()
    {
        var (credentials, region) = Profile();
        var group = $"/pondhawk-logging/test-{Guid.NewGuid():N}";
        const string Stream = "live/orders";

        using var client = new AmazonCloudWatchLogsClient(credentials, region);

        try
        {
            await client.CreateLogGroupAsync(new CreateLogGroupRequest { LogGroupName = group });
            await client.PutRetentionPolicyAsync(new PutRetentionPolicyRequest { LogGroupName = group, RetentionInDays = 7 });

            // Two factories, one after the other: the second meets the stream the first made.
            foreach (var run in new[] { "first run", "second run" })
            {
                var factory = LoggerFactory.Create(b => b.AddCloudWatch(group, "orders", o =>
                {
                    o.Credentials = credentials;
                    o.Region = region;
                    o.StreamName = Stream;
                }));

                factory.CreateLogger("Pondhawk.Logging.Live").LogWarning("{Run}", run);
                factory.Dispose();
            }

            var events = await Read(client, group, Stream, expected: 2);
            events.Select(e => e.GetProperty("Title").GetString()).ShouldBe(["first run", "second run"]);

            var groups = await client.DescribeLogGroupsAsync(new DescribeLogGroupsRequest { LogGroupNamePrefix = group });
            groups.LogGroups.Single(g => string.Equals(g.LogGroupName, group, StringComparison.Ordinal)).RetentionInDays.ShouldBe(7);
        }
        finally
        {
            await Delete(client, group);
        }
    }

    private static Exception Thrown()
    {
        try
        {
            throw new System.InvalidOperationException("live");
        }
        catch (Exception caught)
        {
            return caught;
        }
    }
}
