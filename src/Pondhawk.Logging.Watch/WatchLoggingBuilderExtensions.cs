// Copyright (c) Pond Hawk Technologies Inc. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System.Net.Http;
using CommunityToolkit.Diagnostics;
using Microsoft.Extensions.DependencyInjection.Extensions;
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
    /// <remarks>
    /// The <see cref="WatchDestination"/> this creates is registered as a singleton, so an application that
    /// needs to change where it logs while running can resolve it and call
    /// <see cref="WatchDestination.Rebind"/>. Applications without a service provider to resolve from can
    /// construct the destination themselves and use
    /// <see cref="AddWatch(ILoggingBuilder, WatchDestination, Action{WatchOptions})"/> instead.
    /// </remarks>
    /// <param name="builder">The logging builder.</param>
    /// <param name="serverUrl">The Watch server URL.</param>
    /// <param name="domain">The domain name for log-event batches (typically the application's name).</param>
    /// <param name="configure">An optional action to customize the Watch options.</param>
    /// <returns>The logging builder for chaining.</returns>
    public static ILoggingBuilder AddWatch(
        this ILoggingBuilder builder,
        string serverUrl,
        string domain,
        Action<WatchOptions>? configure = null)
    {
        Guard.IsNotNull(builder);
        Guard.IsNotNullOrWhiteSpace(serverUrl);
        Guard.IsNotNullOrWhiteSpace(domain);

        var options = new WatchOptions { ServerUrl = serverUrl, Domain = domain };
        configure?.Invoke(options);

        return AddWatchCore(builder, new WatchDestination(options.ServerUrl, options.Domain), options);
    }

    /// <summary>
    /// Adds Watch to the logging builder, delivering to a caller-owned <see cref="WatchDestination"/>. Hold
    /// that destination and call <see cref="WatchDestination.Rebind"/> to move the process's log events to
    /// a different Watch server or domain while it runs, without rebuilding the logging factory and without
    /// dropping events already buffered.
    /// </summary>
    /// <param name="builder">The logging builder.</param>
    /// <param name="destination">The server and domain to deliver to.</param>
    /// <param name="configure">
    /// An optional action to customize the Watch options. <see cref="WatchOptions.ServerUrl"/> and
    /// <see cref="WatchOptions.Domain"/> are ignored here — <paramref name="destination"/> supplies both,
    /// and it stays the authority for them after a rebind.
    /// </param>
    /// <returns>The logging builder for chaining.</returns>
    public static ILoggingBuilder AddWatch(
        this ILoggingBuilder builder,
        WatchDestination destination,
        Action<WatchOptions>? configure = null)
    {
        Guard.IsNotNull(builder);
        Guard.IsNotNull(destination);

        var options = new WatchOptions { ServerUrl = destination.ServerUrl, Domain = destination.Domain };
        configure?.Invoke(options);

        return AddWatchCore(builder, destination, options);
    }

    private static ILoggingBuilder AddWatchCore(
        ILoggingBuilder builder,
        WatchDestination destination,
        WatchOptions options)
    {
        // The destination carries absolute URIs, so the client needs no base address and keeps working
        // across a rebind to a different server.
        var httpClient = new HttpClient();
        var switches = new WatchSwitchSource(httpClient, destination, options.PollInterval);
        switches.WhenNotMatched(options.DefaultLevel, options.DefaultColor);
        switches.Start();

        // Resolvable for applications that would rather look the destination up than capture it.
        builder.Services.TryAddSingleton(destination);

        // The processor owns the HTTP client and switch source created here for it, disposing them on shutdown.
        return builder.AddWatch(httpClient, switches, destination, options, ownsDependencies: true);
    }

    /// <summary>
    /// Wires the Watch filter and ZLogger processor onto the builder from a supplied HTTP client and switch
    /// source. The public <c>AddWatch</c> overloads create those; this one lets tests inject controlled ones.
    /// </summary>
    internal static ILoggingBuilder AddWatch(
        this ILoggingBuilder builder,
        HttpClient httpClient,
        SwitchSource switches,
        WatchDestination destination,
        WatchOptions options,
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
                destination,
                options.BatchSize,
                options.FlushInterval,
                ownsDependencies));

        return builder;
    }
}
