// Copyright (c) Pond Hawk Technologies Inc. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using Microsoft.Extensions.Logging;

namespace Pondhawk.Logging.Tests.Support;

/// <summary>
/// A Microsoft.Extensions.Logging <see cref="ILogger"/> that captures emitted entries in memory for
/// assertion — the rendered message plus the structured state key/values the Pondhawk logging API attaches.
/// </summary>
internal sealed class CollectingLogger : ILogger
{
    private readonly LogLevel _minimumLevel;

    /// <summary>Creates a logger; <paramref name="minimumLevel"/> defaults to Trace so every level is captured.</summary>
    public CollectingLogger(LogLevel minimumLevel = LogLevel.Trace) => _minimumLevel = minimumLevel;

    public List<Entry> Entries { get; } = [];

    public bool IsEnabled(LogLevel logLevel) => logLevel != LogLevel.None && logLevel >= _minimumLevel;

    public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

    public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception exception, Func<TState, Exception, string> formatter)
    {
        if (!IsEnabled(logLevel))
            return;

        var properties = state as IReadOnlyList<KeyValuePair<string, object>> ?? [];
        Entries.Add(new Entry(logLevel, formatter(state, exception), properties, exception));
    }

    /// <summary>Returns the value of a captured state property, or null when absent.</summary>
    public static object Prop(Entry entry, string key)
    {
        foreach (var property in entry.State)
        {
            if (property.Key == key)
                return property.Value;
        }

        return null;
    }

    internal sealed record Entry(
        LogLevel Level,
        string Message,
        IReadOnlyList<KeyValuePair<string, object>> State,
        Exception Exception);

    private sealed class NullScope : IDisposable
    {
        public static readonly NullScope Instance = new();

        public void Dispose()
        {
        }
    }
}
