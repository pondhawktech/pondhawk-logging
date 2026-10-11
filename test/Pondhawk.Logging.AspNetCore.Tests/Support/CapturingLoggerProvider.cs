// Copyright (c) Pond Hawk Technologies Inc. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;

namespace Pondhawk.Logging.AspNetCore.Tests.Support;

/// <summary>
/// A logging provider that keeps what is logged, with what <see cref="CorrelationManager"/> held at the time —
/// what a sink would stamp on the event.
/// </summary>
internal sealed class CapturingLoggerProvider : ILoggerProvider
{
    public const string DiagnosticsCategory = "Pondhawk.Diagnostics.Http";

    private readonly ConcurrentQueue<Entry> _entries = new();

    /// <summary>The level the diagnostics category is enabled at; <see cref="LogLevel.None"/> turns it off.</summary>
    public LogLevel DiagnosticsLevel { get; init; } = LogLevel.Debug;

    /// <summary>
    /// Whether any other category is enabled. ASP.NET Core only starts an Activity for a request when its own
    /// hosting logger is, so turning this off is how a test gets a request with no Activity.
    /// </summary>
    public bool OtherCategories { get; init; } = true;

    public IReadOnlyList<Entry> Entries => [.. _entries];

    /// <summary>The diagnostics category's events, in order.</summary>
    public IReadOnlyList<Entry> Diagnostics => [.. _entries.Where(e => e.Category == DiagnosticsCategory)];

    /// <summary>
    /// Everything the diagnostics category logged as one text — messages and payloads — for "this never appears"
    /// assertions. Only that category: ASP.NET Core's own hosting logger writes the whole URL at Information.
    /// </summary>
    public string Everything => string.Join('\n', Diagnostics.Select(e => e.Message + "\n" + e.Payload));

    public ILogger CreateLogger(string categoryName) => new Logger(this, categoryName);

    public void Dispose()
    {
    }

    internal sealed record Entry(
        string Category,
        LogLevel Level,
        string Message,
        string Payload,
        string CorrelationId,
        string Subject,
        string Tenant);

    private sealed class Logger(CapturingLoggerProvider provider, string category) : ILogger
    {
        public IDisposable BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel)
        {
            if (logLevel == LogLevel.None)
                return false;

            return category == DiagnosticsCategory
                ? provider.DiagnosticsLevel != LogLevel.None && logLevel >= provider.DiagnosticsLevel
                : provider.OtherCategories;
        }

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception exception, Func<TState, Exception, string> formatter)
        {
            if (!IsEnabled(logLevel))
                return;

            string payload = null;

            if (state is IReadOnlyList<KeyValuePair<string, object>> properties)
                payload = properties.FirstOrDefault(p => p.Key == LogPropertyNames.PayloadContent).Value as string;

            provider._entries.Enqueue(new Entry(
                category, logLevel, formatter(state, exception), payload,
                CorrelationManager.Current, CorrelationManager.Subject, CorrelationManager.Tenant));
        }
    }
}
