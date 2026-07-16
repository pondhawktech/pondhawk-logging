# Pondhawk.Logging.Watch

The Watch Server provider for [`Pondhawk.Logging`](../Pondhawk.Logging/README.md): a Serilog
`ILogEventSink` with Channel-based batching, dynamic switch-based level control, and a switch-aware
`ILoggerSource`. The structured logging API itself (method tracing, object serialization, typed
payloads, `[Sensitive]` masking) lives in `Pondhawk.Logging`; this package delivers those events to a
Watch Server and makes the API switch-aware.

## Quick Start

### Configure Serilog for Watch

```csharp
using Pondhawk.Logging.Watch;
using Serilog;

// Recommended — Watch Server controls log levels via switches
Log.Logger = new LoggerConfiguration()
    .UseWatch("http://localhost:11000", "MyApp")
    .CreateLogger();

// Advanced — manual MinimumLevel and sink configuration
Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Verbose()
    .WriteTo.Watch("http://localhost:11000", "MyApp")
    .CreateLogger();
```

### Switch-aware logger acquisition

`UseWatch`/`Watch` have out-param overloads that expose the internally-created `SwitchSource`. Share
that one instance with a `WatchLoggerSource` so the call-site logging-API guards and the sink filter
read the same live switch table:

```csharp
using Pondhawk.Logging;
using Pondhawk.Logging.Watch;
using Serilog;

Log.Logger = new LoggerConfiguration()
    .UseWatch("http://localhost:11000", "MyApp", out var switchSource)
    .CreateLogger();

// Register as the app's ILoggerSource (DI shown conceptually)
ILoggerSource loggers = new WatchLoggerSource(Log.Logger, switchSource);
```

Loggers handed out by a `WatchLoggerSource` are switch-aware: `LogObject`/`LogJson`/etc. skip
serialization for switch-dropped categories, because the API gates on `ILogger.IsEnabled` and the
`WatchLogger` consults the live switch level for its category.

### Running alongside other sinks (e.g. Grafana Loki)

The Watch sink is a standard Serilog `ILogEventSink`, so it composes with any off-the-shelf
sink — every event fans out to all configured sinks. A common production setup pairs Watch
(rich, live developer view) with [Grafana Loki](https://grafana.com/oss/loki/) (durable,
queryable store) via [`Serilog.Sinks.Grafana.Loki`](https://www.nuget.org/packages/Serilog.Sinks.Grafana.Loki):

```csharp
using Serilog;
using Serilog.Events;
using Serilog.Sinks.Grafana.Loki;

Log.Logger = new LoggerConfiguration()
    .UseWatch("http://watch:11000", "MyApp")           // Verbose; Watch Server drives filtering via switches
    .WriteTo.GrafanaLoki(
        "http://loki:3100",
        labels: new[] { new LokiLabel { Key = "app", Value = "myapp" } },
        restrictedToMinimumLevel: LogEventLevel.Information)   // keep Verbose out of Loki storage
    .CreateLogger();
```

Two things to keep in mind when adding a second sink next to Watch:

- **`UseWatch` sets `MinimumLevel.Verbose()`** so the Watch Server can control filtering through
  switches. That global minimum applies to *every* sink, so a storage sink like Loki will receive
  Verbose too unless you constrain it per-sink with `restrictedToMinimumLevel:` (as above).
- **Label cardinality.** The logging API attaches a `Pondhawk.PayloadContent` property that can hold a
  large JSON payload (from `LogObject`/`LogJson`). Keep it — and any other high-cardinality property —
  in the log line body, never promoted to a Loki label. Reserve labels for low-cardinality dimensions
  (`app`, `env`, `service`); indexing a large or unique value as a label will blow up Loki's index.

### Use the Logging API

The logging API is a set of extensions on Serilog's `ILogger` and lives in `Pondhawk.Logging`
(`using Pondhawk.Logging;`). Obtain a logger by injecting an `ILoggerSource` and calling
`CreateLogger<T>()` — when that source is a `WatchLoggerSource`, the calls below become switch-aware.
`Log.ForContext<T>()` still works as a fallback but is not switch-aware.

```csharp
using Pondhawk.Logging;

public class OrderService
{
    private readonly ILogger _logger;

    public OrderService(ILoggerSource loggers)
    {
        _logger = loggers.CreateLogger<OrderService>();
    }

    public void ProcessOrder(int orderId)
    {
        using var _ = _logger.EnterMethod();

        _logger.Debug("Loading order {OrderId}", orderId);
        var order = LoadOrder(orderId);
        _logger.LogObject(order);
    }
}
```

### Method Tracing

```csharp
public async Task ProcessAsync(int orderId)
{
    using var _ = _logger.EnterMethod();
    // Logs "Entering ProcessAsync" at Verbose level (the class comes from SourceContext)
    // On dispose: "Exiting ProcessAsync (elapsed ms)"
}
```

### Object Serialization

```csharp
logger.LogObject(order);                       // Serialize to JSON payload
logger.LogObject("Fetched Order", order);      // With custom title

// Typed payloads with syntax highlighting hints
logger.LogJson("API Response", jsonString);
logger.LogSql("Query", sqlString);
logger.LogXml("Configuration", xmlString);
logger.LogYaml("Settings", yamlString);
logger.LogText("Output", textString);
```

### Sensitive Data Masking

```csharp
public class Credentials
{
    public string Username { get; set; }

    [Sensitive]
    public string Password { get; set; }  // Logged as "Sensitive - HasValue: true"
}
```

## Key Components

- **WatchSink** -- `ILogEventSink` with unbounded `Channel` batching. Converts Serilog events to Watch `LogEvent` instances.
- **Switching** -- Dynamic log level control via `SwitchSource`/`SwitchDef` with pattern matching (longest prefix wins). `WatchSwitchSource` polls a Watch Server for switch configuration.
- **WatchLogger / WatchLoggerSource** -- switch-aware `ILogger` and its `ILoggerSource`; loggers consult the live switch table so the logging API skips serialization for switch-dropped categories.
- **HTTP delivery** -- Posts event batches to Watch Server with circuit breaker and critical event buffering.
- **LogEvent / LogEventBatch** -- MemoryPack-serializable event model with Brotli compression.

## Architecture

Events flow: Serilog `ILogger` -> WatchSink (Channel queue) -> Background batch task -> HTTP delivery to Watch Server.

Switch-based filtering checks the source context pattern against configured switches. Longest prefix match wins. Version-based invalidation ensures cached loggers see switch updates without recreation.

The logging API types (`SerilogExtensions`, `MethodLogger`, `PayloadType`, `SensitiveAttribute`, `LogPropertyNames`, the serializers) live in `Pondhawk.Logging`, which this package references. This package adds only the Watch-specific pieces: the sink, switching, and the switch-aware `WatchLogger`/`WatchLoggerSource`.

## Documentation

See [CLAUDE.md](CLAUDE.md) for detailed AI development guidance on this package, and
[`../Pondhawk.Logging/CLAUDE.md`](../Pondhawk.Logging/CLAUDE.md) for the full logging-API guide.
