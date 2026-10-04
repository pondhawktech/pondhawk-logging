// Copyright (c) Pond Hawk Technologies Inc. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using Amazon.CloudWatchLogs;
using Amazon.Util;
using CommunityToolkit.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using ZLogger;

namespace Pondhawk.Logging.CloudWatch;

/// <summary>
/// <see cref="ILoggingBuilder"/> extensions that add Amazon CloudWatch Logs as a logging destination: each
/// event is one JSON object in the stream <c>&lt;instance-id&gt;/&lt;service&gt;</c> of a log group.
/// </summary>
public static class CloudWatchLoggingBuilderExtensions
{
    /// <summary>
    /// One attempt of at most this long per call. No SDK retries: a failure pauses the provider for a
    /// minute instead (<see cref="CloudWatchLoggerProcessor.PauseAfterFailure"/>), so an outage costs the
    /// flush one bounded call a minute.
    /// </summary>
    public static readonly TimeSpan CallTimeout = TimeSpan.FromSeconds(5);

    /// <summary>The number of times the SDK retries a failed call: none. See <see cref="CallTimeout"/>.</summary>
    public const int MaxErrorRetry = 0;

    /// <summary>
    /// The lowest level sent to CloudWatch, fixed: Information, Warning, Error and Critical always go, Debug
    /// and Trace never do. There is no setting and no switch.
    /// </summary>
    public const LogLevel MinimumLevel = LogLevel.Information;

    /// <summary>
    /// Adds CloudWatch Logs to the logging builder, writing Information and above to the stream
    /// <c>&lt;instance-id&gt;/&lt;service&gt;</c> of <paramref name="logGroup"/>.
    /// </summary>
    /// <remarks>
    /// Nothing here touches the network or can stop the host starting: the AWS client is built, the instance
    /// id read and the stream created on the first batch, on the provider's flush thread. The
    /// <see cref="CloudWatchDestination"/> this creates is registered as a singleton, so an application can
    /// resolve it and call <see cref="CloudWatchDestination.Rebind"/>.
    /// </remarks>
    /// <param name="builder">The logging builder.</param>
    /// <param name="logGroup">The log group to write to. Created, with retention, if it does not exist.</param>
    /// <param name="service">The service name — the second part of the default stream name.</param>
    /// <param name="configure">An optional action to customize the CloudWatch options.</param>
    /// <returns>The logging builder for chaining.</returns>
    /// <exception cref="ArgumentException"><paramref name="logGroup"/> is not a valid log group name.</exception>
    public static ILoggingBuilder AddCloudWatch(
        this ILoggingBuilder builder,
        string logGroup,
        string service,
        Action<CloudWatchOptions>? configure = null)
    {
        return AddCloudWatch(builder, new CloudWatchDestination(logGroup), service, configure);
    }

    /// <summary>
    /// Adds CloudWatch Logs to the logging builder, writing to a caller-owned
    /// <see cref="CloudWatchDestination"/>. Hold that destination and call
    /// <see cref="CloudWatchDestination.Rebind"/> to change the log group while the process runs — or start
    /// it <see cref="CloudWatchDestination.Unbound"/> when the group is not known until later.
    /// </summary>
    /// <param name="builder">The logging builder.</param>
    /// <param name="destination">The log group to write to.</param>
    /// <param name="service">The service name — the second part of the default stream name.</param>
    /// <param name="configure">An optional action to customize the CloudWatch options.</param>
    /// <returns>The logging builder for chaining.</returns>
    public static ILoggingBuilder AddCloudWatch(
        this ILoggingBuilder builder,
        CloudWatchDestination destination,
        string service,
        Action<CloudWatchOptions>? configure = null)
    {
        Guard.IsNotNull(builder);
        Guard.IsNotNull(destination);
        Guard.IsNotNullOrWhiteSpace(service);

        var options = new CloudWatchOptions();
        configure?.Invoke(options);

        // Built by the processor on its first batch, not here: the constructor throws when no region can
        // be found, and nothing in logging may stop a service starting.
        IAmazonCloudWatchLogs Client()
        {
            var config = new AmazonCloudWatchLogsConfig { Timeout = CallTimeout, MaxErrorRetry = MaxErrorRetry };
            if (options.Region is not null)
                config.RegionEndpoint = options.Region;

            return options.Credentials is not null
                ? new AmazonCloudWatchLogsClient(options.Credentials, config)
                : new AmazonCloudWatchLogsClient(config);
        }

        return builder.AddCloudWatch(Client, destination, service, options, () => EC2InstanceMetadata.InstanceId, ownsClient: true);
    }

    /// <summary>
    /// Wires the fixed CloudWatch level floor and provider onto the builder from a supplied client factory and
    /// instance-id source. The public <c>AddCloudWatch</c> overloads create those; this one lets tests inject
    /// controlled ones.
    /// </summary>
    internal static ILoggingBuilder AddCloudWatch(
        this ILoggingBuilder builder,
        Func<IAmazonCloudWatchLogs> clientFactory,
        CloudWatchDestination destination,
        string service,
        CloudWatchOptions options,
        Func<string?> instanceId,
        bool ownsClient)
    {
        // Resolvable for applications that would rather look the destination up than capture it.
        builder.Services.TryAddSingleton(destination);

        // Fixed at Information, and scoped to this provider, so it neither follows nor disturbs any other
        // provider's level. A provider-scoped rule is also more specific than Watch's switch filter, so the
        // switch table does not gate CloudWatch: it gets this floor whatever the switches say.
        builder.AddFilter<CloudWatchLoggerProvider>(category: null, MinimumLevel);

        builder.Services.AddSingleton<ILoggerProvider>(_ =>
            new CloudWatchLoggerProvider(
                new CloudWatchLoggerProcessor(clientFactory, destination, service, options, instanceId, ownsClient),
                new ZLoggerOptions()));

        return builder;
    }
}
