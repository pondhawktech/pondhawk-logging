// Copyright (c) Pond Hawk Technologies Inc. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System.Diagnostics;
using System.Globalization;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;

namespace Pondhawk.Logging;

/// <summary>
/// A disposable <see cref="ILogger"/> wrapper returned by
/// <see cref="LoggingExtensions.EnterMethod"/>. It delegates all <see cref="ILogger"/> members to the
/// inner logger and, on dispose, logs method exit with elapsed time at <see cref="LogLevel.Debug"/>
/// (tagged with a <see cref="LogPropertyNames.Nesting"/> delta of -1) — whenever the entry was logged.
/// </summary>
public sealed class MethodLogger : ILogger, IDisposable
{
    private readonly ILogger _logger;
    private readonly string _method;
    private readonly long _startTimestamp;
    private readonly bool _entered;
    private bool _disposed;

    /// <summary>The level method entry and exit are logged at.</summary>
    internal const LogLevel Level = LogLevel.Debug;

    // One inert MethodLogger per inner logger, handed out whenever the entry is not logged. Weak on the logger, so
    // a logger that goes away takes its inert instance with it.
    private static readonly ConditionalWeakTable<ILogger, MethodLogger> InertByLogger = new();

    private MethodLogger(ILogger logger, string method, bool entered)
    {
        _logger = logger;
        _method = method;
        _startTimestamp = entered ? Stopwatch.GetTimestamp() : 0;
        _entered = entered;
    }

    /// <summary>A scope whose entry was logged: its exit is logged on dispose.</summary>
    internal static MethodLogger Entered(ILogger logger, string method) => new(logger, method, entered: true);

    /// <summary>
    /// A scope whose entry was not logged: dispose does nothing, and the <see cref="ILogger"/> members still go to
    /// <paramref name="logger"/>. Shared per logger, so a disabled <c>EnterMethod</c> allocates nothing.
    /// </summary>
    internal static MethodLogger Inert(ILogger logger)
    {
        if (InertByLogger.TryGetValue(logger, out var inert))
            return inert;

        return InertByLogger.GetValue(logger, static l => new MethodLogger(l, string.Empty, entered: false));
    }

    /// <summary>
    /// Logs method exit with elapsed time and a <see cref="LogPropertyNames.Nesting"/> delta of -1. The exit
    /// is logged whenever the entry was, without asking <see cref="ILogger.IsEnabled"/> again, so a level
    /// lowered while the method ran does not leave the entry unpaired here. A filter in the logging
    /// pipeline (a Watch switch, for one) is still applied to the exit and can drop it.
    /// </summary>
    public void Dispose()
    {
        // An inert scope is shared, so it keeps no state of its own.
        if (!_entered || _disposed)
            return;

        _disposed = true;

        var elapsed = Stopwatch.GetElapsedTime(_startTimestamp);
        var elapsedMs = elapsed.TotalMilliseconds.ToString("F2", CultureInfo.InvariantCulture);
        var title = "Exiting " + _method + " (" + elapsedMs + "ms)";
        var properties = new KeyValuePair<string, object?>[] { new(LogPropertyNames.Nesting, -1) };
        var state = new LogState(title, properties);
        _logger.Log(Level, default, state, null, LogState.Formatter);
    }

    /// <inheritdoc />
    public IDisposable? BeginScope<TState>(TState state)
        where TState : notnull
        => _logger.BeginScope(state);

    /// <inheritdoc />
    public bool IsEnabled(LogLevel logLevel) => _logger.IsEnabled(logLevel);

    /// <inheritdoc />
    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter)
        => _logger.Log(logLevel, eventId, state, exception, formatter);
}
