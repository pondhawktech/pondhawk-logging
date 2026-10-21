// Copyright (c) Pond Hawk Technologies Inc. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using Amazon;
using Amazon.Runtime;
using CommunityToolkit.Diagnostics;

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

    private readonly List<string> _categories = [];
    private bool? _only;

    /// <summary>
    /// Sends only these categories to this registration's log group, and any category they are a dotted prefix
    /// of (<c>Orders.Access</c> takes <c>Orders.Access.Bff</c>, not <c>Orders.AccessX</c>), ignoring case. Every
    /// other category is never enabled for this provider: not formatted, not queued. Can be called more than
    /// once; cannot be combined with <see cref="ExceptCategories"/>.
    /// </summary>
    /// <param name="categories">The category names.</param>
    /// <returns>These options, for chaining.</returns>
    /// <exception cref="ArgumentException">A name is null, empty or whitespace.</exception>
    /// <exception cref="InvalidOperationException"><see cref="ExceptCategories"/> was called on these options.</exception>
    public CloudWatchOptions OnlyCategories(params string[] categories) => Route(categories, only: true);

    /// <summary>
    /// Sends every category to this registration's log group except these, and any category they are a dotted
    /// prefix of, ignoring case. Those are never enabled for this provider: not formatted, not queued. Can be
    /// called more than once; cannot be combined with <see cref="OnlyCategories"/>.
    /// </summary>
    /// <param name="categories">The category names.</param>
    /// <returns>These options, for chaining.</returns>
    /// <exception cref="ArgumentException">A name is null, empty or whitespace.</exception>
    /// <exception cref="InvalidOperationException"><see cref="OnlyCategories"/> was called on these options.</exception>
    public CloudWatchOptions ExceptCategories(params string[] categories) => Route(categories, only: false);

    /// <summary>The categories this registration sends, from <see cref="OnlyCategories"/> or <see cref="ExceptCategories"/>.</summary>
    internal CategoryRoute Categories => _only is { } only ? new CategoryRoute(_categories, only) : CategoryRoute.All;

    private CloudWatchOptions Route(string[] categories, bool only)
    {
        Guard.IsNotNull(categories);
        if (_only is { } set && set != only)
            throw new InvalidOperationException("OnlyCategories and ExceptCategories cannot both be set on one AddCloudWatch registration; register CloudWatch twice instead.");

        foreach (var category in categories)
            Guard.IsNotNullOrWhiteSpace(category, nameof(categories));

        _only = only;
        _categories.AddRange(categories);
        return this;
    }
}
