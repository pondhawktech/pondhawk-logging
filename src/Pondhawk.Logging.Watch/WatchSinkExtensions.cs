// Copyright (c) Pond Hawk Technologies Inc. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using CommunityToolkit.Diagnostics;
using Serilog;
using Serilog.Configuration;
using Serilog.Events;

namespace Pondhawk.Logging.Watch;

/// <summary>
/// Serilog configuration extensions for the Watch sink.
/// </summary>
public static class WatchSinkExtensions
{
    /// <summary>
    /// Configures Serilog to use Watch for level control and log delivery.
    /// Sets <c>MinimumLevel.Verbose()</c> so that the Watch Server's switch configuration
    /// controls filtering, then adds the Watch sink.
    /// </summary>
    /// <param name="config">The Serilog logger configuration.</param>
    /// <param name="serverUrl">The Watch Server URL.</param>
    /// <param name="domain">The domain name for log event batches.</param>
    /// <returns>The logger configuration for chaining.</returns>
    /// <example>
    /// <code>
    /// Log.Logger = new LoggerConfiguration()
    ///     .UseWatch("http://localhost:11000", "MyApp")
    ///     .CreateLogger();
    /// </code>
    /// </example>
    public static LoggerConfiguration UseWatch(
        this LoggerConfiguration config,
        string serverUrl,
        string domain)
    {
        return UseWatch(config, serverUrl, domain, _ => { });
    }

    /// <summary>
    /// Configures Serilog to use Watch for level control and log delivery.
    /// Sets <c>MinimumLevel.Verbose()</c> so that the Watch Server's switch configuration
    /// controls filtering, then adds the Watch sink with custom options.
    /// </summary>
    /// <param name="config">The Serilog logger configuration.</param>
    /// <param name="serverUrl">The Watch Server URL.</param>
    /// <param name="domain">The domain name for log event batches.</param>
    /// <param name="configure">An action to customize the sink options.</param>
    /// <returns>The logger configuration for chaining.</returns>
    public static LoggerConfiguration UseWatch(
        this LoggerConfiguration config,
        string serverUrl,
        string domain,
        Action<WatchSinkOptions> configure)
    {
        Guard.IsNotNull(config);

        return config
            .MinimumLevel.Verbose()
            .WriteTo.Watch(serverUrl, domain, configure);
    }

    /// <summary>
    /// Like <see cref="UseWatch(LoggerConfiguration, string, string)"/> but also exposes the
    /// <see cref="SwitchSource"/> so it can be shared with a <see cref="WatchLoggerSource"/> for
    /// switch-aware, call-site payload skipping.
    /// </summary>
    /// <param name="config">The Serilog logger configuration.</param>
    /// <param name="serverUrl">The Watch Server URL.</param>
    /// <param name="domain">The domain name for log event batches.</param>
    /// <param name="switchSource">Receives the switch source the sink was wired with.</param>
    /// <returns>The logger configuration for chaining.</returns>
    public static LoggerConfiguration UseWatch(
        this LoggerConfiguration config,
        string serverUrl,
        string domain,
        out SwitchSource switchSource)
    {
        return UseWatch(config, serverUrl, domain, _ => { }, out switchSource);
    }

    /// <summary>
    /// Like <see cref="UseWatch(LoggerConfiguration, string, string, Action{WatchSinkOptions})"/> but
    /// also exposes the <see cref="SwitchSource"/> for sharing with a <see cref="WatchLoggerSource"/>.
    /// </summary>
    /// <param name="config">The Serilog logger configuration.</param>
    /// <param name="serverUrl">The Watch Server URL.</param>
    /// <param name="domain">The domain name for log event batches.</param>
    /// <param name="configure">An action to customize the sink options.</param>
    /// <param name="switchSource">Receives the switch source the sink was wired with.</param>
    /// <returns>The logger configuration for chaining.</returns>
    public static LoggerConfiguration UseWatch(
        this LoggerConfiguration config,
        string serverUrl,
        string domain,
        Action<WatchSinkOptions> configure,
        out SwitchSource switchSource)
    {
        Guard.IsNotNull(config);

        return config
            .MinimumLevel.Verbose()
            .WriteTo.Watch(serverUrl, domain, configure, out switchSource);
    }

    /// <summary>
    /// Adds a Watch sink using just a server URL and domain.
    /// Creates the HttpClient and WatchSwitchSource internally.
    /// </summary>
    /// <param name="config">The Serilog sink configuration.</param>
    /// <param name="serverUrl">The Watch Server URL.</param>
    /// <param name="domain">The domain name for log event batches.</param>
    /// <returns>The logger configuration for chaining.</returns>
    public static LoggerConfiguration Watch(
        this LoggerSinkConfiguration config,
        string serverUrl,
        string domain)
    {
        return Watch(config, serverUrl, domain, _ => { });
    }

    /// <summary>
    /// Adds a Watch sink using a server URL, domain, and options customization.
    /// Creates the HttpClient and WatchSwitchSource internally.
    /// </summary>
    /// <param name="config">The Serilog sink configuration.</param>
    /// <param name="serverUrl">The Watch Server URL.</param>
    /// <param name="domain">The domain name for log event batches.</param>
    /// <param name="configure">An action to customize the sink options.</param>
    /// <returns>The logger configuration for chaining.</returns>
    public static LoggerConfiguration Watch(
        this LoggerSinkConfiguration config,
        string serverUrl,
        string domain,
        Action<WatchSinkOptions> configure)
    {
        return Watch(config, serverUrl, domain, configure, out _);
    }

    /// <summary>
    /// Adds a Watch sink and exposes the internally-created <see cref="SwitchSource"/> so it can be
    /// shared with a <see cref="WatchLoggerSource"/> — the call-site switch-aware guard and the sink
    /// filter then read one source of truth.
    /// </summary>
    /// <param name="config">The Serilog sink configuration.</param>
    /// <param name="serverUrl">The Watch Server URL.</param>
    /// <param name="domain">The domain name for log event batches.</param>
    /// <param name="configure">An action to customize the sink options.</param>
    /// <param name="switchSource">Receives the switch source the sink was wired with.</param>
    /// <returns>The logger configuration for chaining.</returns>
    public static LoggerConfiguration Watch(
        this LoggerSinkConfiguration config,
        string serverUrl,
        string domain,
        Action<WatchSinkOptions> configure,
        out SwitchSource switchSource)
    {
        Guard.IsNotNull(config);
        Guard.IsNotNullOrWhiteSpace(serverUrl);
        Guard.IsNotNullOrWhiteSpace(domain);
        Guard.IsNotNull(configure);

        var options = new WatchSinkOptions { ServerUrl = serverUrl, Domain = domain };
        configure(options);

        var normalizedUrl = options.ServerUrl.TrimEnd('/') + "/";
        var httpClient = new HttpClient { BaseAddress = new Uri(normalizedUrl) };
        var source = new WatchSwitchSource(httpClient, options.Domain, options.PollInterval);
        source.WhenNotMatched(options.DefaultLevel, options.DefaultColor);
        source.Start();

        // These dependencies were created here for the sink, so the sink owns their disposal
        // (the low-level WatchSink overload below leaves caller-supplied dependencies alone).
        var sink = new WatchSink(
            httpClient,
            source,
            options.Domain,
            options.BatchSize,
            options.FlushInterval,
            ownsDependencies: true);

        switchSource = source;
        return config.Sink(sink);
    }

    /// <summary>
    /// Adds a Watch sink to the Serilog configuration with Channel-based batching.
    /// This is the low-level API for advanced scenarios where you manage the HttpClient
    /// and SwitchSource yourself.
    /// </summary>
    /// <param name="config">The Serilog sink configuration.</param>
    /// <param name="httpClient">The HTTP client configured for the Watch Server.</param>
    /// <param name="switchSource">The switch source for log level control.</param>
    /// <param name="domain">The domain name for log event batches.</param>
    /// <param name="batchSize">The batch size before flushing.</param>
    /// <param name="flushInterval">The flush interval when batch size is not reached.</param>
    /// <returns>The logger configuration for chaining.</returns>
    public static LoggerConfiguration WatchSink(
        this LoggerSinkConfiguration config,
        HttpClient httpClient,
        SwitchSource switchSource,
        string domain = "Default",
        int batchSize = 100,
        TimeSpan? flushInterval = null)
    {
        Guard.IsNotNull(config);
        Guard.IsNotNull(httpClient);
        Guard.IsNotNull(switchSource);

        switchSource.Start();

        var watchSink = new WatchSink(httpClient, switchSource, domain, batchSize, flushInterval);

        return config.Sink(watchSink);
    }
}
