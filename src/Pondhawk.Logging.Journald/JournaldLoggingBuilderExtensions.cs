// Copyright (c) Pond Hawk Technologies Inc. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using ZLogger;

namespace Pondhawk.Logging.Journald;

/// <summary>
/// <see cref="ILoggingBuilder"/> extensions that add the journal as a logging destination: each event written over
/// journald's native protocol, its parts as journal fields.
/// </summary>
public static class JournaldLoggingBuilderExtensions
{
    /// <summary>
    /// Adds the journald sink, writing <paramref name="floor"/> and above.
    /// </summary>
    /// <remarks>
    /// <para>
    /// Under systemd — the journal's socket there and <c>JOURNAL_STREAM</c> naming this process's stdout or
    /// stderr — events go to the journal's socket; otherwise to the console as the line
    /// <c>AddJournaldConsole</c> writes. That is decided once, when the provider is created.
    /// </para>
    /// <para>
    /// The floor is a rule scoped to this provider, placed before the host's own rules, so configuration under
    /// <c>Logging:Journald:LogLevel</c> — <c>Default</c> or a category — overrides it. The host's provider-less
    /// category rules apply as well: an event must pass both.
    /// </para>
    /// <para>
    /// Each event is sent synchronously, inside the logging call; nothing is queued, so nothing is lost when
    /// the process exits and disposing has nothing to flush.
    /// </para>
    /// </remarks>
    /// <param name="builder">The logging builder.</param>
    /// <param name="floor">The lowest level written, Information unless configuration says otherwise.</param>
    /// <param name="configure">An optional action to customize the options.</param>
    /// <returns>The logging builder for chaining.</returns>
    public static ILoggingBuilder AddJournald(
        this ILoggingBuilder builder,
        LogLevel floor = LogLevel.Information,
        Action<JournaldOptions>? configure = null) =>
        builder.AddJournald(floor, configure, static () => JournalDetection.OpenIfUnderJournal(), stdout: null);

    /// <summary>
    /// Wires the sink from a supplied journal and stdout. The public overload detects the journal; this one lets
    /// tests decide.
    /// </summary>
    internal static ILoggingBuilder AddJournald(
        this ILoggingBuilder builder,
        LogLevel floor,
        Action<JournaldOptions>? configure,
        Func<JournalSocket?> journal,
        Action<string>? stdout)
    {
        ArgumentNullException.ThrowIfNull(builder);

        var options = new JournaldOptions();
        configure?.Invoke(options);
        var syslogIdentifier = string.IsNullOrWhiteSpace(options.SyslogIdentifier) ? DefaultSyslogIdentifier() : options.SyslogIdentifier;

        // First in the list: among rules as specific as this one, MEL takes the last, so configuration wins.
        builder.Services.Configure<LoggerFilterOptions>(o =>
            o.Rules.Insert(0, new LoggerFilterRule(typeof(JournaldLoggerProvider).FullName, categoryName: null, floor, filter: null)));

        builder.Services.AddSingleton<ILoggerProvider>(services =>
            new JournaldLoggerProvider(
                new JournaldLoggerProcessor(journal(), syslogIdentifier, stdout),
                new ZLoggerOptions(),
                new CategoryFloors(services.GetService<IOptionsMonitor<LoggerFilterOptions>>())));

        return builder;
    }

    // What journald names a service's stdout lines when the unit sets no SyslogIdentifier=: its executable's name.
    private static string DefaultSyslogIdentifier()
    {
        var name = Path.GetFileName(Environment.ProcessPath);
        return string.IsNullOrEmpty(name) ? AppDomain.CurrentDomain.FriendlyName : name;
    }
}
