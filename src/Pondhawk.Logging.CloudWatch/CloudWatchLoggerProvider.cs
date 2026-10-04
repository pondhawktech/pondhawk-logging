// Copyright (c) Pond Hawk Technologies Inc. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using Microsoft.Extensions.Logging;
using ZLogger;
using ZLogger.Providers;

namespace Pondhawk.Logging.CloudWatch;

/// <summary>
/// The ZLogger provider that delivers to CloudWatch Logs. It exists as its own type so the CloudWatch
/// level floor can be a filter scoped to it: every ZLogger processor otherwise shares one provider type,
/// and a floor scoped to that would also clamp the Watch provider registered beside this one.
/// </summary>
[ProviderAlias("CloudWatch")]
public sealed class CloudWatchLoggerProvider : ZLoggerLogProcessorLoggerProvider
{
    /// <summary>Creates the provider over a CloudWatch processor.</summary>
    /// <param name="processor">The processor that delivers to CloudWatch Logs.</param>
    /// <param name="options">The ZLogger options the provider's loggers use.</param>
    public CloudWatchLoggerProvider(CloudWatchLoggerProcessor processor, ZLoggerOptions options)
        : base(processor, options)
    {
    }
}
