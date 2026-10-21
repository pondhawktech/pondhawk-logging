// Copyright (c) Pond Hawk Technologies Inc. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using ZLogger;
using ZLogger.Providers;

namespace Pondhawk.Logging.CloudWatch;

/// <summary>
/// The ZLogger provider that delivers to CloudWatch Logs. It exists as its own type so the CloudWatch
/// level floor can be a filter scoped to it: every ZLogger processor otherwise shares one provider type,
/// and a floor scoped to that would also clamp the Watch provider registered beside this one. It also
/// routes by category (<see cref="CloudWatchOptions.OnlyCategories"/>,
/// <see cref="CloudWatchOptions.ExceptCategories"/>): a category it does not send gets a logger that is never
/// enabled, so its events cost this provider nothing.
/// </summary>
[ProviderAlias("CloudWatch")]
public sealed class CloudWatchLoggerProvider : ZLoggerLogProcessorLoggerProvider, ILoggerProvider
{
    private readonly CategoryRoute _route;

    /// <summary>Creates the provider over a CloudWatch processor.</summary>
    /// <param name="processor">The processor that delivers to CloudWatch Logs.</param>
    /// <param name="options">The ZLogger options the provider's loggers use.</param>
    public CloudWatchLoggerProvider(CloudWatchLoggerProcessor processor, ZLoggerOptions options)
        : this(processor, options, CategoryRoute.All)
    {
    }

    internal CloudWatchLoggerProvider(CloudWatchLoggerProcessor processor, ZLoggerOptions options, CategoryRoute route)
        : base(processor, options)
    {
        _route = route;
    }

    /// <summary>
    /// Creates a logger for <paramref name="categoryName"/>: one that delivers to CloudWatch, or, for a category
    /// this provider does not send, one that is never enabled.
    /// </summary>
    /// <param name="categoryName">The category of the logger.</param>
    /// <returns>The logger.</returns>
    // ZLogger's CreateLogger is not virtual; re-implementing ILoggerProvider routes the factory's calls here.
    public new ILogger CreateLogger(string categoryName) =>
        _route.Includes(categoryName) ? base.CreateLogger(categoryName) : NullLogger.Instance;
}
