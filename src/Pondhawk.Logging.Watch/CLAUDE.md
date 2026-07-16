# Pondhawk.Logging.Watch - AI Development Guide

## Overview

Pondhawk.Logging.Watch is the **Watch Server provider** for [`Pondhawk.Logging`](../Pondhawk.Logging/CLAUDE.md).
It supplies a Serilog `ILogEventSink` with Channel-based batching, dynamic switch-based level control,
and a switch-aware `ILoggerSource` (`WatchLogger`/`WatchLoggerSource`). It delivers events over HTTP to
a Watch Server with circuit-breaker resilience.

The structured logging **API itself** — `EnterMethod`, `Inspect`, `LogObject`, the typed-payload
helpers, `[Sensitive]` masking, and the `LogPropertyNames` contract — lives in `Pondhawk.Logging`, which
this package references. See [`../Pondhawk.Logging/CLAUDE.md`](../Pondhawk.Logging/CLAUDE.md) for the full
logging guide.

Targets `net10.0` (single target — no conditional compilation). References `Pondhawk.Logging`; no
dependency on Pondhawk.Core.

---

## Logging Guidelines (brief — full guide in Pondhawk.Logging)

The logging conventions below are part of the `Pondhawk.Logging` API (`using Pondhawk.Logging;`). This
is a condensed reminder; the authoritative version, with the complete extension-method reference, is in
[`../Pondhawk.Logging/CLAUDE.md`](../Pondhawk.Logging/CLAUDE.md).

**Logging is the primary debugging tool.** You cannot attach a debugger in production, but you can
always read logs.

- **Start methods with `EnterMethod`** — `using var _ = _logger.EnterMethod();`
- **Logging IS comments** — write a `logger.Debug(...)` instead of a code comment; logs are visible in production.
- **Log calculated/fetched values** — `logger.Inspect("discount", discount);`
- **`LogObject` for complex types** — captures full state, catches throwing getters, respects `[Sensitive]`.
- **Mark sensitive data** — `[Sensitive]` on properties masks them to `"Sensitive - HasValue: true"`.
- **Provide context / exception context** — include IDs, states, values.

```csharp
using Pondhawk.Logging;

public class OrderService
{
    private readonly ILogger _logger;

    public OrderService(ILoggerSource loggers)
    {
        _logger = loggers.CreateLogger<OrderService>();
    }

    public async Task<Order> ProcessOrderAsync(int orderId)
    {
        using var _ = _logger.EnterMethod();

        _logger.Debug("Loading order from database");
        var order = await _repository.GetOrderAsync(orderId);
        _logger.LogObject(order);

        return order;
    }
}
```

Prefer injecting an `ILoggerSource` and calling `CreateLogger<T>()`; `Log.ForContext<T>()` is the
fallback. When the injected source is a `WatchLoggerSource`, these calls become switch-aware with no
code change.

---

## Key Concepts

### Switches (Dynamic Log Levels)

- Switches control logging level and color per category pattern
- Fetched from Watch Server via HTTP (`WatchSwitchSource`), cached with version-based invalidation
- Pattern matching: longest prefix wins ("MyApp.Data" beats "MyApp")

### Color (UI Visualization)

- Color comes from Switch configuration, NOT from application code
- Applied automatically to every LogEvent
- Used in Watch Server UI for visual category grouping

### Nesting (Method Tracing)

- `EnterMethod()` (from `Pondhawk.Logging`) sets Nesting = +1, dispose sets Nesting = -1
- Watch viewers render as collapsible method hierarchy
- Includes elapsed time measurement

### PayloadType (Syntax Highlighting)

- Json, Sql, Xml, Yaml, Text for UI syntax highlighting
- Use `LogJson()`, `LogSql()`, `LogXml()` etc. for explicit types
- `LogObject()` automatically uses Json type

## WatchLogger / WatchLoggerSource

`WatchLoggerSource` (public `ILoggerSource`) hands out `WatchLogger` instances (internal `ILogger`).
`WatchLogger.IsEnabled(level)` consults the live `SwitchSource.Lookup(category).Level` instead of a
static Serilog minimum. Because the whole `Pondhawk.Logging` API gates on `ILogger.IsEnabled`, acquiring
loggers from a `WatchLoggerSource` makes `LogObject`/`LogPayload`/etc. switch-aware — payloads are not
serialized when the category's switch has dropped that level — while callers just hold a plain `ILogger`.

To wire this up, share one `SwitchSource` between the sink and the source using the out-param overloads
of `UseWatch` / `Watch`:

```csharp
using Pondhawk.Logging;
using Pondhawk.Logging.Watch;
using Serilog;

Log.Logger = new LoggerConfiguration()
    .UseWatch("http://localhost:11000", "MyApp", out var switchSource)
    .CreateLogger();

// The source shares the sink's switch table, so call-site guards and the
// sink filter read one source of truth.
ILoggerSource loggers = new WatchLoggerSource(Log.Logger, switchSource);
```

`UseWatch(..., out SwitchSource)` and `Watch(..., out SwitchSource)` (and their options-taking
overloads) expose the internally-created switch source for exactly this purpose.

## Extension Method Reference

The extension methods (`EnterMethod`, `Inspect`, `LogObject`, `LogJson`/`LogSql`/`LogXml`/`LogYaml`/`LogText`)
are defined in `Pondhawk.Logging`. See [`../Pondhawk.Logging/CLAUDE.md`](../Pondhawk.Logging/CLAUDE.md)
for the complete reference.

## Sink Configuration

```csharp
using Pondhawk.Logging.Watch;
using Serilog;

// Recommended — Watch Server controls log levels via switches
Log.Logger = new LoggerConfiguration()
    .UseWatch("http://localhost:11000", "MyApp")
    .CreateLogger();

// With options
Log.Logger = new LoggerConfiguration()
    .UseWatch("http://localhost:11000", "MyApp", opts =>
    {
        opts.BatchSize = 50;
        opts.PollInterval = TimeSpan.FromSeconds(15);
    })
    .CreateLogger();

// Advanced — manual control over MinimumLevel
Log.Logger = new LoggerConfiguration()
    .MinimumLevel.Verbose()
    .WriteTo.Watch("http://localhost:11000", "MyApp")
    .CreateLogger();
```

## Architecture Notes

### Channel-Based Batching

- `WatchSink.Emit()` writes to an unbounded channel (non-blocking)
- Background task drains channel by batch size or flush interval
- Converts Serilog `LogEvent` → Watch `LogEvent` with switch-based filtering
- Flushes remaining events on dispose

### Circuit Breaker (HTTP Sink)

- Opens after N consecutive failures (`FailureThreshold`, default: 3)
- Critical events (Warning/Error) buffered during outage (`MaxCriticalBufferSize`)
- Non-critical events dropped
- Exponential backoff with max delay

### Logging API ↔ Sink Communication

The logging API in `Pondhawk.Logging` writes well-known Serilog property names, defined by the public
`LogPropertyNames` contract in that package:
- `Pondhawk.Nesting` — method tracing depth (+1 enter, -1 exit)
- `Pondhawk.PayloadType` — int value of `PayloadType` enum
- `Pondhawk.PayloadContent` — serialized payload string

`WatchSink` reads these properties from the Serilog `LogEvent` and maps them to the Watch `LogEvent` model.

## Performance Guidelines

1. **Switch-aware acquisition**: Acquiring loggers from a `WatchLoggerSource` makes the client-side
   `LogObject`/`LogPayload` guards switch-aware — payloads are not serialized for switch-dropped
   categories, because `WatchLogger.IsEnabled` consults the live switch level. A plain `Log.ForContext<T>()`
   logger does **not** get this: under `UseWatch` its `IsEnabled(Verbose)` is always true, so payloads
   serialize regardless of switches (the sink still drops the event, but after serialization).
2. **Enabled Levels**: LogEvent allocation, JSON serialization for payloads
3. **Batching**: Events queued to channel, batched for HTTP delivery
4. **Hot Path**: Avoid string interpolation before level check

```csharp
// Good - no allocation if disabled
logger.Debug("User {UserId} logged in", userId);

// Bad - string allocated even if disabled
logger.Debug($"User {userId} logged in");
```

## Project Structure

```
src/Pondhawk.Logging.Watch/
  # Sink + configuration
  WatchSink.cs                          # ILogEventSink with channel batching + circuit breaker
  WatchSinkExtensions.cs                # Serilog LoggerConfiguration extensions (UseWatch / Watch, + out-param overloads)
  WatchSinkOptions.cs                   # Options for the UseWatch / Watch convenience methods

  # Switch-aware logger source
  WatchLogger.cs                        # Internal ILogger whose IsEnabled consults the live switch table
  WatchLoggerSource.cs                  # Public ILoggerSource handing out WatchLogger (shares a SwitchSource with the sink)

  # Switching
  Switch.cs                             # Switch model (Pattern, Tag, Level, Color)
  SwitchDef.cs                          # Switch definition DTO
  SwitchDto.cs                          # Wire format for HTTP switch updates
  SwitchesResponse.cs                   # HTTP response model
  SwitchSource.cs                       # Local switch source with pattern matching
  WatchSwitchSource.cs                  # Polls Watch Server for switch configuration

  # Event model + serialization
  LogEvent.cs                           # Core event model (MemoryPackable)
  LogEventBatch.cs                      # Batch container
  LogEventBatchSerializer.cs            # MemoryPack+Brotli wire; JSON for debug/testing
  LogEventBatchContext.cs               # STJ source-gen context (JSON debug/testing)

  GlobalUsings.cs                       # Shared usings for the project
```

The logging API types (`SerilogExtensions`, `MethodLogger`, `CorrelationManager`, `SensitiveAttribute`,
`PayloadType`, `LogPropertyNames`, `Serializers/*`, `TypeExtensions`) live in `Pondhawk.Logging`, not
this package.

## Common Mistakes

- Don't use string interpolation: `$"User {user}"` allocates even when disabled
- Do use structured logging: `"User {UserId}", userId`
- Do use `EnterMethod()` for method-level tracing
- Do use appropriate PayloadType for syntax highlighting
- Don't set color in application code — it comes from Switch configuration
- To get switch-aware call-site guards, inject a `WatchLoggerSource` (share its `SwitchSource` with the sink); a bare `Log.ForContext<T>()` is not switch-aware
