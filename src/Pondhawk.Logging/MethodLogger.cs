// Copyright (c) Pond Hawk Technologies Inc. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System.Diagnostics;
using System.Globalization;
using Microsoft.Extensions.Logging;

namespace Pondhawk.Logging;

/// <summary>
/// A disposable <see cref="ILogger"/> wrapper returned by
/// <see cref="LoggingExtensions.EnterMethod"/>. It delegates all <see cref="ILogger"/> members to the
/// inner logger and, on dispose, logs method exit with elapsed time (tagged with a
/// <see cref="LogPropertyNames.Nesting"/> delta of -1).
/// </summary>
public sealed class MethodLogger : ILogger, IDisposable
{
    private readonly ILogger _logger;
    private readonly string _method;
    private readonly long _startTimestamp;
    private readonly bool _tracing;
    private bool _disposed;

    internal MethodLogger(ILogger logger, string method, bool tracing)
    {
        _logger = logger;
        _method = method;
        _startTimestamp = Stopwatch.GetTimestamp();
        _tracing = tracing;
    }

    /// <summary>Logs method exit with elapsed time and a <see cref="LogPropertyNames.Nesting"/> delta of -1.</summary>
    public void Dispose()
    {
        if (_disposed)
            return;

        _disposed = true;

        if (_tracing && _logger.IsEnabled(LogLevel.Trace))
        {
            var elapsed = Stopwatch.GetElapsedTime(_startTimestamp);
            var elapsedMs = elapsed.TotalMilliseconds.ToString("F2", CultureInfo.InvariantCulture);
            var title = "Exiting " + _method + " (" + elapsedMs + "ms)";
            var properties = new KeyValuePair<string, object?>[] { new(LogPropertyNames.Nesting, -1) };
            var state = new LogState(title, properties);
            _logger.Log(LogLevel.Trace, default, state, null, LogState.Formatter);
        }
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
