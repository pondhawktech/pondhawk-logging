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
    /// <param name="watchUrl">
    /// The Watch URL — <c>scheme://[key@]host[:port][/base-path]/&lt;domain&gt;</c>, e.g.
    /// <c>http://localhost:11000/my-app</c> or <c>https://pwk_id_secret@watch.example.com/my-app</c>. The last
    /// path segment is the domain; the optional user-info is a Watch API key.
    /// </param>
    /// <param name="configure">An optional action to customize the Watch options.</param>
    /// <returns>The logging builder for chaining.</returns>
    /// <exception cref="ArgumentException"><paramref name="watchUrl"/> is not a valid Watch URL.</exception>
    public static ILoggingBuilder AddWatch(
        this ILoggingBuilder builder,
        string watchUrl,
        Action<WatchOptions>? configure = null)
    {
        Guard.IsNotNull(builder);

        var options = new WatchOptions { Url = watchUrl };
        configure?.Invoke(options);

        return AddWatchCore(builder, new WatchDestination(options.Url), options);
    }

    /// <summary>
    /// Adds Watch to the logging builder, delivering to a caller-owned <see cref="WatchDestination"/>. Hold
    /// that destination and call <see cref="WatchDestination.Rebind"/> to move the process's log events to
    /// a different Watch URL while it runs, without rebuilding the logging factory and without
    /// dropping events already buffered.
    /// </summary>
    /// <param name="builder">The logging builder.</param>
    /// <param name="destination">The Watch URL to deliver to.</param>
    /// <param name="configure">
    /// An optional action to customize the Watch options. <see cref="WatchOptions.Url"/> is ignored here —
    /// <paramref name="destination"/> supplies it, and stays the authority for it after a rebind.
    /// </param>
    /// <returns>The logging builder for chaining.</returns>
    public static ILoggingBuilder AddWatch(
        this ILoggingBuilder builder,
        WatchDestination destination,
        Action<WatchOptions>? configure = null)
    {
        Guard.IsNotNull(builder);
        Guard.IsNotNull(destination);

        var options = new WatchOptions { Url = destination.Url };
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
