// Copyright (c) Pond Hawk Technologies Inc. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System.Reflection;
using Amazon.CloudWatchLogs;
using Amazon.CloudWatchLogs.Model;

namespace Pondhawk.Logging.CloudWatch.Tests.Support;

/// <summary>
/// The CloudWatch Logs calls the provider makes, in memory, with switches for the ways they fail in
/// practice.
/// </summary>
public class FakeCloudWatchLogs : DispatchProxy
{
    private readonly Lock _gate = new();

    /// <summary>The group has not been created: stream and put calls report it missing until something creates it.</summary>
    public bool GroupMissing { get; set; }

    /// <summary>Another host creates the group between this one's stream call and its own create.</summary>
    public bool GroupCreatedElsewhere { get; set; }

    /// <summary>The role may write to a group but not create one.</summary>
    public bool CreateGroupDenied { get; set; }

    /// <summary>Setting retention fails after the group was created.</summary>
    public bool RetentionFails { get; set; }

    /// <summary>The role has no logs permissions.</summary>
    public bool Denied { get; set; }

    /// <summary>The role has no logs permissions on this one group.</summary>
    public string DeniedGroup { get; set; }

    /// <summary>The stream is already there, as after a service restart.</summary>
    public bool StreamExists { get; set; }

    /// <summary>PutLogEvents fails, as a throttle or outage would.</summary>
    public bool PutFails { get; set; }

    /// <summary>CreateLogStream fails this many more times, as an outage would, then works.</summary>
    public int StreamOutages { get; set; }

    public List<string> GroupsCreated { get; } = [];
    public List<PutRetentionPolicyRequest> Retentions { get; } = [];
    public List<CreateLogStreamRequest> Streams { get; } = [];
    public List<PutLogEventsRequest> Puts { get; } = [];

    /// <summary>Every call made, of any kind.</summary>
    public int Calls { get; private set; }

    public int StreamCount
    {
        get
        {
            lock (_gate)
                return Streams.Count;
        }
    }

    /// <summary>Every message put so far, in the order it was sent.</summary>
    public List<string> Messages
    {
        get
        {
            lock (_gate)
                return Puts.SelectMany(p => p.LogEvents).Select(e => e.Message).ToList();
        }
    }

    public static (IAmazonCloudWatchLogs Client, FakeCloudWatchLogs Fake) Create()
    {
        var client = Create<IAmazonCloudWatchLogs, FakeCloudWatchLogs>();
        return (client, (FakeCloudWatchLogs)(object)client);
    }

    protected override object Invoke(MethodInfo method, object[] args)
    {
        if (string.Equals(method?.Name, nameof(IDisposable.Dispose), StringComparison.Ordinal))
            return null;

        lock (_gate)
            Calls++;

        return method?.Name switch
        {
            nameof(IAmazonCloudWatchLogs.CreateLogStreamAsync) when args?[0] is CreateLogStreamRequest create => CreateStream(create),
            nameof(IAmazonCloudWatchLogs.PutLogEventsAsync) when args?[0] is PutLogEventsRequest put => Put(put),
            nameof(IAmazonCloudWatchLogs.CreateLogGroupAsync) when args?[0] is CreateLogGroupRequest group => CreateGroup(group),
            nameof(IAmazonCloudWatchLogs.PutRetentionPolicyAsync) when args?[0] is PutRetentionPolicyRequest retention => Retain(retention),
            _ => throw new NotSupportedException($"FakeCloudWatchLogs does not implement {method?.Name}"),
        };
    }

    private Task<CreateLogStreamResponse> CreateStream(CreateLogStreamRequest request)
    {
        lock (_gate)
        {
            // Recorded before failing: a denied attempt is still an attempt.
            Streams.Add(request);
            Trouble(request.LogGroupName);
            if (StreamOutages > 0)
            {
                StreamOutages--;
                throw new ServiceUnavailableException("The service cannot complete the request");
            }

            if (StreamExists)
                throw new ResourceAlreadyExistsException("The specified log stream already exists");
            return Task.FromResult(new CreateLogStreamResponse());
        }
    }

    private Task<PutLogEventsResponse> Put(PutLogEventsRequest request)
    {
        lock (_gate)
        {
            Trouble(request.LogGroupName);
            if (PutFails)
                throw new ServiceUnavailableException("The service cannot complete the request");
            Puts.Add(request);
            return Task.FromResult(new PutLogEventsResponse());
        }
    }

    private Task<CreateLogGroupResponse> CreateGroup(CreateLogGroupRequest request)
    {
        lock (_gate)
        {
            if (Denied || CreateGroupDenied)
                throw new AccessDeniedException("User is not authorized to perform: logs:CreateLogGroup");
            GroupMissing = false;
            if (GroupCreatedElsewhere)
                throw new ResourceAlreadyExistsException("The specified log group already exists");
            GroupsCreated.Add(request.LogGroupName);
            return Task.FromResult(new CreateLogGroupResponse());
        }
    }

    private Task<PutRetentionPolicyResponse> Retain(PutRetentionPolicyRequest request)
    {
        lock (_gate)
        {
            if (RetentionFails)
                throw new AccessDeniedException("User is not authorized to perform: logs:PutRetentionPolicy");
            Retentions.Add(request);
            return Task.FromResult(new PutRetentionPolicyResponse());
        }
    }

    private void Trouble(string logGroup)
    {
        if (Denied || string.Equals(DeniedGroup, logGroup, StringComparison.Ordinal))
            throw new AccessDeniedException("User is not authorized to perform: logs:CreateLogStream");
        if (GroupMissing)
            throw new ResourceNotFoundException("The specified log group does not exist");
    }
}
