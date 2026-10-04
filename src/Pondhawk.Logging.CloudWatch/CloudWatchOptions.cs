// Copyright (c) Pond Hawk Technologies Inc. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using Amazon;
using Amazon.Runtime;

namespace Pondhawk.Logging.CloudWatch;

/// <summary>
/// Configuration options for <see cref="CloudWatchLoggingBuilderExtensions"/>'s <c>AddCloudWatch</c>.
/// </summary>
public class CloudWatchOptions
{
    /// <summary>
    /// Gets or sets the log stream name. Null — the default — names the stream
    /// <c>&lt;instance-id&gt;/&lt;service&gt;</c> from EC2 instance metadata, which turns CloudWatch off on
    /// a host that has no instance id. Set it to write from anywhere else (ECS, Lambda, a workstation).
    /// </summary>
    public string? StreamName { get; set; }

    /// <summary>
    /// Gets or sets the credentials the CloudWatch Logs client uses. Null uses the SDK's default chain
    /// (the instance role, environment, profile).
    /// </summary>
    public AWSCredentials? Credentials { get; set; }

    /// <summary>
    /// Gets or sets the region the CloudWatch Logs client uses. Null lets the SDK find it (AWS_REGION, the
    /// profile, instance metadata).
    /// </summary>
    public RegionEndpoint? Region { get; set; }

    /// <summary>
    /// Gets or sets the retention, in days, given to a log group this provider creates. A group that already
    /// exists keeps whatever retention it has. Default is 30.
    /// </summary>
    public int RetentionDays { get; set; } = 30;

    /// <summary>Gets or sets the number of events that triggers a send. Default is 500.</summary>
    public int BatchSize { get; set; } = 500;

    /// <summary>Gets or sets how long a partial batch waits before it is sent. Default is one second.</summary>
    public TimeSpan FlushInterval { get; set; } = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Gets or sets how many Warning-and-above events are held while there is nowhere to send them — the
    /// destination is unbound, or CloudWatch is being left alone after a failure. The most recent are kept.
    /// Default is 100.
    /// </summary>
    public int MaxHeldEvents { get; set; } = 100;
}
