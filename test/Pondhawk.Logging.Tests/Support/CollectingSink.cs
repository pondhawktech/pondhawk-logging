// Copyright (c) Pond Hawk Technologies Inc. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using Serilog;
using Serilog.Core;
using Serilog.Events;
using SerilogEvent = Serilog.Events.LogEvent;

namespace Pondhawk.Logging.Tests.Support;

/// <summary>
/// A Serilog sink that captures emitted events in memory for assertion.
/// </summary>
internal sealed class CollectingSink : ILogEventSink
{
    public List<SerilogEvent> Events { get; } = [];

    public void Emit(SerilogEvent logEvent) => Events.Add(logEvent);

    /// <summary>
    /// Builds a Serilog logger that writes to a fresh <see cref="CollectingSink"/>.
    /// </summary>
    /// <param name="minimumLevel">The minimum level; defaults to Verbose so every level is captured.</param>
    public static (ILogger Logger, CollectingSink Sink) Build(LogEventLevel minimumLevel = LogEventLevel.Verbose)
    {
        var sink = new CollectingSink();
        var logger = new LoggerConfiguration()
            .MinimumLevel.Is(minimumLevel)
            .WriteTo.Sink(sink)
            .CreateLogger();
        return (logger, sink);
    }

    public static string? Text(SerilogEvent e) => e.MessageTemplate.Text;

    public static object? Prop(SerilogEvent e, string name) =>
        e.Properties.TryGetValue(name, out var v) && v is ScalarValue sv ? sv.Value : null;
}
