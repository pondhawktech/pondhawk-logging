// Copyright (c) Pond Hawk Technologies Inc. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System.Net.Http;
using CommunityToolkit.Diagnostics;
using Microsoft.Extensions.Logging;
using ZLogger;

namespace Pondhawk.Logging.Watch;

/// <summary>
/// <see cref="ILoggingBuilder"/> extensions that wire Watch as the application's logging destination:
/// the Watch server's dynamic switches control per-category level (and color), and log events are
/// delivered to the server via a ZLogger processor.
/// </summary>
public static class WatchLoggingBuilderExtensions
{
    /// <summary>
    /// Adds Watch to the logging builder. Starts polling the Watch server for switch configuration,
    /// registers a filter that makes those switches the level gate (evaluated at <c>IsEnabled</c>, so a
    /// switch-dropped category is never formatted), and registers the ZLogger provider with the Watch
    /// delivery processor.
    /// </summary>
    /// <param name="builder">The logging builder.</param>
    /// <param name="serverUrl">The Watch server URL.</param>
    /// <param name="domain">The domain name for log-event batches (typically the application's name).</param>
    /// <param name="configure">An optional action to customize the Watch options.</param>
    /// <returns>The logging builder for chaining.</returns>
    public static ILoggingBuilder AddWatch(
        this ILoggingBuilder builder,
        string serverUrl,
        string domain,
        Action<WatchSinkOptions>? configure = null)
    {
        Guard.IsNotNull(builder);
        Guard.IsNotNullOrWhiteSpace(serverUrl);
        Guard.IsNotNullOrWhiteSpace(domain);

        var options = new WatchSinkOptions { ServerUrl = serverUrl, Domain = domain };
        configure?.Invoke(options);

        var normalizedUrl = options.ServerUrl.TrimEnd('/') + "/";
        var httpClient = new HttpClient { BaseAddress = new Uri(normalizedUrl) };
        var switches = new WatchSwitchSource(httpClient, options.Domain, options.PollInterval);
        switches.WhenNotMatched(options.DefaultLevel, options.DefaultColor);
        switches.Start();

        // The processor owns the HTTP client and switch source created here for it, disposing them on shutdown.
        return builder.AddWatch(httpClient, switches, options, ownsDependencies: true);
    }

    /// <summary>
    /// Wires the Watch filter and ZLogger processor onto the builder from a supplied HTTP client and switch
    /// source. The public <see cref="AddWatch(ILoggingBuilder, string, string, Action{WatchSinkOptions})"/>
    /// creates those; this overload lets tests inject controlled ones.
    /// </summary>
    internal static ILoggingBuilder AddWatch(
        this ILoggingBuilder builder,
        HttpClient httpClient,
        SwitchSource switches,
        WatchSinkOptions options,
        bool ownsDependencies)
    {
        // Open the MEL floor so the switch filter is the sole gate. ZLogger's own IsEnabled is always
        // true, so the composite logger's IsEnabled — the one the call site checks before formatting —
        // reflects this filter, giving switch-dropped categories a zero-work short-circuit.
        builder.SetMinimumLevel(LogLevel.Trace);
        builder.AddFilter((category, level) =>
            string.IsNullOrWhiteSpace(category) || level >= switches.Lookup(category).Level);

        builder.AddZLoggerLogProcessor((_, _) =>
            new WatchLoggerProcessor(
                httpClient,
                switches,
                options.Domain,
                options.BatchSize,
                options.FlushInterval,
                ownsDependencies));

        return builder;
    }
}
