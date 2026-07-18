# Pondhawk.Logging.Watch - AI Development Guide

## Overview

Pondhawk.Logging.Watch is the **Watch Server provider** for [`Pondhawk.Logging`](../Pondhawk.Logging/CLAUDE.md).
It supplies a ZLogger-based `Microsoft.Extensions.Logging` provider with Channel-based batching, dynamic
switch-based level control, and MemoryPack delivery over HTTP to a Watch Server with circuit-breaker
resilience.

The structured logging **API itself** — `EnterMethod`, `Inspect`, `LogObject`, the typed-payload
helpers, `[Sensitive]` masking, and the `LogPropertyNames` contract — lives in `Pondhawk.Logging`, which
this package references. See [`../Pondhawk.Logging/CLAUDE.md`](../Pondhawk.Logging/CLAUDE.md) for the full
logging guide.

Targets `net8.0` (single target — no conditional compilation). References `Pondhawk.Logging`; no
dependency on other Pondhawk packages.

---

## Logging Guidelines (brief — full guide in Pondhawk.Logging)

The logging conventions below are part of the `Pondhawk.Logging` API (`using Pondhawk.Logging;`). This
is a condensed reminder; the authoritative version, with the complete extension-method reference, is in
[`../Pondhawk.Logging/CLAUDE.md`](../Pondhawk.Logging/CLAUDE.md).

**Logging is the primary debugging tool.** You cannot attach a debugger in production, but you can
always read logs.

- **Start methods with `EnterMethod`** — `using var _ = _logger.EnterMethod();`
- **Logging IS comments** — write a `logger.LogDebug(...)` instead of a code comment; logs are visible in production.
- **Log calculated/fetched values** — `logger.Inspect("discount", discount);`
- **`LogObject` for complex types** — captures full state, catches throwing getters, respects `[Sensitive]`.
- **Mark sensitive data** — `[Sensitive]` on properties masks them to `"Sensitive - HasValue: true"`.
- **Provide context / exception context** — include IDs, states, values.

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

    public async Task<Order> ProcessOrderAsync(int orderId)
    {
        using var _ = _logger.EnterMethod();

        _logger.LogDebug("Loading order from database");
        var order = await _repository.GetOrderAsync(orderId);
        _logger.LogObject(order);

        return order;
    }
}
```

Acquire loggers from the standard `ILoggerFactory` (`CreateLogger<T>()`). When Watch is configured, these
calls become switch-aware with no code change — see below.

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

## Switch-based level gating

`AddWatch` registers a `Microsoft.Extensions.Logging` filter that matches a logger's category against the
live switch table (`SwitchSource.Lookup(category).Level`) and gates by the switch's level. ZLogger's own
`IsEnabled` is always true, so the composite logger's `IsEnabled` — the value the call site checks before
formatting — reflects the switch filter. Because the whole `Pondhawk.Logging` API gates on
`ILogger.IsEnabled`, `LogObject`/`LogJson`/etc. skip serialization for switch-dropped categories at the
call site, with no change to calling code — callers just hold a plain `ILogger`.

The switch table is polled from the Watch Server by `WatchSwitchSource`; updates take effect on the next
log call (version-based invalidation) without recreating loggers.

## Extension Method Reference

The extension methods (`EnterMethod`, `Inspect`, `LogObject`, `LogJson`/`LogSql`/`LogXml`/`LogYaml`/`LogText`)
are defined in `Pondhawk.Logging`. See [`../Pondhawk.Logging/CLAUDE.md`](../Pondhawk.Logging/CLAUDE.md)
for the complete reference.

## Configuration

```csharp
using Microsoft.Extensions.Logging;
using Pondhawk.Logging.Watch;

// Recommended — Watch Server controls log levels via switches.
builder.Logging.ClearProviders();
builder.Logging.AddWatch("http://localhost:11000", "MyApp");

// With options
builder.Logging.AddWatch("http://localhost:11000", "MyApp", opts =>
{
    opts.BatchSize = 50;
    opts.PollInterval = TimeSpan.FromSeconds(15);
});

// Standalone factory
using var factory = LoggerFactory.Create(b => b.AddWatch("http://localhost:11000", "MyApp"));
```

## Architecture Notes

### Channel-Based Batching

- `WatchLoggerProcessor.Post()` runs on the calling thread: it captures the correlation id from `Activity.Current`, converts the pooled ZLogger entry to a Watch `LogEvent`, returns the entry, and writes to an unbounded channel (non-blocking)
- A background task drains the channel by batch size or flush interval and posts the batches
- Converts a ZLogger entry (`IZLoggerEntry`) → Watch `LogEvent`, applying the matching switch's color and tag
- Flushes remaining events on dispose

### Circuit Breaker (HTTP delivery)

- Opens after N consecutive failures (`FailureThreshold`, default: 3)
- Critical events (Warning/Error) buffered during outage (`MaxCriticalBufferSize`)
- Non-critical events dropped
- Exponential backoff with max delay

### Logging API ↔ Provider Communication

The logging API in `Pondhawk.Logging` attaches well-known log-state properties, defined by the public
`LogPropertyNames` contract in that package:
- `Pondhawk.Nesting` — method tracing depth (+1 enter, -1 exit)
- `Pondhawk.PayloadType` — int value of `PayloadType` enum
- `Pondhawk.PayloadContent` — serialized payload string

`WatchLoggerProcessor` reads these from the ZLogger entry's structured state and maps them to the Watch
`LogEvent` model.

## Performance Guidelines

1. **Switch gating at the call site**: `AddWatch`'s filter makes `IsEnabled` switch-aware, so the
   `LogObject`/`LogJson` guards skip serialization for switch-dropped categories *before* any work — the
   payload is never built. For message logging, ZLogger's interpolated `ZLogDebug($"...")` methods extend
   the same zero-work short-circuit to the interpolated arguments.
2. **Enabled Levels**: LogEvent allocation, JSON serialization for payloads
3. **Batching**: Events queued to channel, batched for HTTP delivery
4. **Hot Path**: Avoid string interpolation with the classic overloads before the level check

```csharp
// Good - structured template; no args array or formatting if disabled at IsEnabled
logger.LogDebug("User {UserId} logged in", userId);

// Best - ZLogger interpolated handler; skips interpolation entirely when disabled
logger.ZLogDebug($"User {userId} logged in");

// Bad - string allocated even if disabled
logger.LogDebug($"User {userId} logged in");
```

## Project Structure

```
src/Pondhawk.Logging.Watch/
  # Provider + configuration
  WatchLoggerProcessor.cs               # ZLogger IAsyncLogProcessor: channel batching + circuit breaker + HTTP
  WatchLoggingBuilderExtensions.cs      # ILoggingBuilder.AddWatch (registers the switch filter + processor)
  WatchOptions.cs                       # Options for AddWatch

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
  LogEventBatchSerializer.cs            # MemoryPack wire; JSON for debug/testing
  LogEventBatchContext.cs               # STJ source-gen context (JSON debug/testing)

  GlobalUsings.cs                       # Shared usings for the project
```

The logging API types (`LoggingExtensions`, `MethodLogger`, `CorrelationManager`, `SensitiveAttribute`,
`PayloadType`, `LogPropertyNames`, `Serializers/*`, `TypeExtensions`) live in `Pondhawk.Logging`, not
this package.

## Common Mistakes

- Don't use string interpolation with the classic overloads: `$"User {user}"` allocates even when disabled
- Do use structured logging (`"User {UserId}", userId`) or ZLogger's interpolated `ZLog*` methods
- Do use `EnterMethod()` for method-level tracing
- Do use appropriate PayloadType for syntax highlighting
- Don't set color in application code — it comes from Switch configuration
