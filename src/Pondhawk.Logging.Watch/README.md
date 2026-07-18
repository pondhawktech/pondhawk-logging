# Pondhawk.Logging.Watch

The Watch Server provider for [`Pondhawk.Logging`](../Pondhawk.Logging/README.md): a ZLogger-based
`Microsoft.Extensions.Logging` provider with Channel-based batching, dynamic switch-based level control,
and MemoryPack delivery to a Watch Server. The structured logging API itself (method tracing, object
serialization, typed payloads, `[Sensitive]` masking) lives in `Pondhawk.Logging`; this package delivers
those events to a Watch Server and makes the API switch-aware.

## Quick Start

### Configure logging for Watch

`AddWatch` registers the ZLogger delivery processor and a level filter driven by the Watch server's
switch table:

```csharp
using Microsoft.Extensions.Logging;
using Pondhawk.Logging.Watch;

// In a Host / WebApplication builder — the Watch Server controls log levels via switches.
builder.Logging.ClearProviders();
builder.Logging.AddWatch("http://localhost:11000", "MyApp");

// Or standalone:
using var factory = LoggerFactory.Create(b => b.AddWatch("http://localhost:11000", "MyApp"));

// Options (batch size, flush/poll intervals, default level/color when no switch matches):
builder.Logging.AddWatch("http://localhost:11000", "MyApp", o =>
{
    o.BatchSize = 200;
    o.PollInterval = TimeSpan.FromSeconds(15);
});
```

Switch-awareness is automatic: `AddWatch` opens the level floor and registers a filter that consults the
live switch table, so the logging API — which gates on `ILogger.IsEnabled` — skips serialization for
switch-dropped categories at the call site, with no change to calling code.

### Use the Logging API

The logging API is a set of extensions on `ILogger` and lives in `Pondhawk.Logging`
(`using Pondhawk.Logging;`). Obtain a logger from the standard `ILoggerFactory`:

```csharp
using Microsoft.Extensions.Logging;
using Pondhawk.Logging;

public class OrderService
{
    private readonly ILogger _logger;

    public OrderService(ILoggerFactory loggers)
    {
        _logger = loggers.CreateLogger<OrderService>();
    }

    public void ProcessOrder(int orderId)
    {
        using var _ = _logger.EnterMethod();

        _logger.LogDebug("Loading order {OrderId}", orderId);
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
    // Logs "Entering ProcessAsync" at Trace level (the category comes from the logger)
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

- **WatchLoggerProcessor** -- a ZLogger `IAsyncLogProcessor` with unbounded `Channel` batching. Converts ZLogger entries to Watch `LogEvent` instances on the calling thread (capturing correlation), then delivers them.
- **Switching** -- Dynamic log level control via `SwitchSource`/`SwitchDef` with pattern matching (longest prefix wins). `WatchSwitchSource` polls a Watch Server for switch configuration. `AddWatch` turns the switch table into a `Microsoft.Extensions.Logging` filter that gates `IsEnabled`.
- **HTTP delivery** -- Posts event batches to the Watch Server with a circuit breaker and critical-event buffering.
- **LogEvent / LogEventBatch** -- MemoryPack-serializable event model.

## Architecture

Events flow: `ILogger` -> ZLogger provider -> `WatchLoggerProcessor` (Channel queue) -> background batch
task -> HTTP delivery to the Watch Server.

The level gate is a `Microsoft.Extensions.Logging` filter (registered by `AddWatch`) that matches a
logger's category against the switch table (longest prefix wins) and compares the switch level. Because
it is evaluated at `IsEnabled` — before the call site formats anything — a switch-dropped category does
zero work. Version-based invalidation lets the polling switch source publish updates that take effect on
the next log call without recreating loggers.

The logging API types (`LoggingExtensions`, `MethodLogger`, `PayloadType`, `SensitiveAttribute`,
`LogPropertyNames`, the serializers) live in `Pondhawk.Logging`, which this package references. This
package adds only the Watch-specific pieces: the delivery processor, switching, and the `AddWatch` wiring.

## Documentation

See [CLAUDE.md](CLAUDE.md) for detailed AI development guidance on this package, and
[`../Pondhawk.Logging/CLAUDE.md`](../Pondhawk.Logging/CLAUDE.md) for the full logging-API guide.
