// Copyright (c) Pond Hawk Technologies Inc. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System.Diagnostics.CodeAnalysis;
using Pondhawk.Logging.Utilities;
using Serilog;
using Serilog.Core;
using Serilog.Events;
using SerilogLogEvent = Serilog.Events.LogEvent;
#pragma warning disable CA2254

namespace Pondhawk.Logging.Watch;

/// <summary>
/// An <see cref="ILogger"/> whose <see cref="IsEnabled"/> consults the live Watch switch table for
/// its category. Because the logging API extensions (LogObject/LogPayload/EnterMethod) gate on
/// <c>IsEnabled</c> — a real interface member that dispatches virtually — wrapping the logger this
/// way makes the entire API switch-aware: payloads are not serialized for switch-dropped categories,
/// while callers still hold a plain <see cref="ILogger"/> and never see this type.
/// </summary>
internal sealed class WatchLogger : ILogger
{
    private readonly ILogger _inner;
    private readonly string _category;
    private readonly SwitchSource _switches;

    public WatchLogger(ILogger inner, string category, SwitchSource switches)
    {
        _inner = inner;
        _category = category;
        _switches = switches;
    }

    /// <inheritdoc />
    public bool IsEnabled(LogEventLevel level)
        => string.IsNullOrWhiteSpace(_category)
            ? _inner.IsEnabled(level)
            : level >= _switches.Lookup(_category).Level;

    // ForContext returns a WatchLogger so the switch-aware guard survives chaining.

    /// <inheritdoc />
    public ILogger ForContext(ILogEventEnricher enricher)
        => new WatchLogger(_inner.ForContext(enricher), _category, _switches);

    /// <inheritdoc />
    public ILogger ForContext(IEnumerable<ILogEventEnricher> enrichers)
        => new WatchLogger(_inner.ForContext(enrichers), _category, _switches);

    /// <inheritdoc />
    public ILogger ForContext(string propertyName, object? value, bool destructureObjects = false)
        => new WatchLogger(_inner.ForContext(propertyName, value, destructureObjects), _category, _switches);

    /// <inheritdoc />
    public ILogger ForContext<TSource>()
        => new WatchLogger(_inner.ForContext<TSource>(), typeof(TSource).GetConciseFullName(), _switches);

    /// <inheritdoc />
    public ILogger ForContext(Type source)
        => new WatchLogger(_inner.ForContext(source), source.GetConciseFullName(), _switches);

    // Every Write overload guards on the switch-aware IsEnabled before delegating, so even raw
    // logging (and Inspect, which has no client guard of its own) is switch-aware.

    /// <inheritdoc />
    public void Write(SerilogLogEvent logEvent)
    {
        if (logEvent is not null && IsEnabled(logEvent.Level))
            _inner.Write(logEvent);
    }

    /// <inheritdoc />
    public void Write(LogEventLevel level, string messageTemplate)
    {
        if (IsEnabled(level))
            _inner.Write(level, messageTemplate);
    }

    /// <inheritdoc />
    public void Write<T>(LogEventLevel level, string messageTemplate, T propertyValue)
    {
        if (IsEnabled(level))
            _inner.Write(level, messageTemplate, propertyValue);
    }

    /// <inheritdoc />
    public void Write<T0, T1>(LogEventLevel level, string messageTemplate, T0 propertyValue0, T1 propertyValue1)
    {
        if (IsEnabled(level))
            _inner.Write(level, messageTemplate, propertyValue0, propertyValue1);
    }

    /// <inheritdoc />
    public void Write<T0, T1, T2>(LogEventLevel level, string messageTemplate, T0 propertyValue0, T1 propertyValue1, T2 propertyValue2)
    {
        if (IsEnabled(level))
            _inner.Write(level, messageTemplate, propertyValue0, propertyValue1, propertyValue2);
    }

    /// <inheritdoc />
    public void Write(LogEventLevel level, string messageTemplate, params object?[]? propertyValues)
    {
        if (IsEnabled(level))
            _inner.Write(level, messageTemplate, propertyValues);
    }

    /// <inheritdoc />
    public void Write(LogEventLevel level, Exception? exception, string messageTemplate)
    {
        if (IsEnabled(level))
            _inner.Write(level, exception, messageTemplate);
    }

    /// <inheritdoc />
    public void Write<T>(LogEventLevel level, Exception? exception, string messageTemplate, T propertyValue)
    {
        if (IsEnabled(level))
            _inner.Write(level, exception, messageTemplate, propertyValue);
    }

    /// <inheritdoc />
    public void Write<T0, T1>(LogEventLevel level, Exception? exception, string messageTemplate, T0 propertyValue0, T1 propertyValue1)
    {
        if (IsEnabled(level))
            _inner.Write(level, exception, messageTemplate, propertyValue0, propertyValue1);
    }

    /// <inheritdoc />
    public void Write<T0, T1, T2>(LogEventLevel level, Exception? exception, string messageTemplate, T0 propertyValue0, T1 propertyValue1, T2 propertyValue2)
    {
        if (IsEnabled(level))
            _inner.Write(level, exception, messageTemplate, propertyValue0, propertyValue1, propertyValue2);
    }

    /// <inheritdoc />
    public void Write(LogEventLevel level, Exception? exception, string messageTemplate, params object?[]? propertyValues)
    {
        if (IsEnabled(level))
            _inner.Write(level, exception, messageTemplate, propertyValues);
    }

    /// <inheritdoc />
    public bool BindMessageTemplate(string messageTemplate, object?[]? propertyValues, [NotNullWhen(true)] out MessageTemplate? parsedTemplate, [NotNullWhen(true)] out IEnumerable<LogEventProperty>? boundProperties)
        => _inner.BindMessageTemplate(messageTemplate, propertyValues, out parsedTemplate, out boundProperties);

    /// <inheritdoc />
    public bool BindProperty(string? propertyName, object? value, bool destructureObjects, [NotNullWhen(true)] out LogEventProperty? property)
        => _inner.BindProperty(propertyName, value, destructureObjects, out property);
}
