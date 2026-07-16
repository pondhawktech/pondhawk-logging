# Pondhawk.Logging - AI Development Guide

## Overview

Pondhawk.Logging is the Serilog-based **structured logging API** plus the **`ILoggerSource`**
acquisition abstraction. It provides method tracing, object serialization, typed payloads, and
`[Sensitive]` masking as extensions on `Serilog.ILogger`. It has **no sink or transport** — provider
packages such as [`Pondhawk.Logging.Watch`](../Pondhawk.Logging.Watch/CLAUDE.md) build on it.

Targets `net10.0` (single target — no conditional compilation). Fully standalone — no dependency on
Pondhawk.Core. Namespace: `Pondhawk.Logging`.

---

## Logging Guidelines

**Logging is the primary debugging tool.** You cannot attach a debugger in production, but you can
always read logs. Well-structured logging tells you exactly what happened and why.

### 1. Start Methods with EnterMethod

Most methods should begin with `EnterMethod()`. Only the simplest methods (one-liners, trivial getters)
skip this.

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

- Obtain a logger by injecting an `ILoggerSource` and calling `CreateLogger<T>()`; it sets `SourceContext` to the concise type name. `Log.ForContext<T>()` is the fallback.
- Use discard `_` for the `EnterMethod()` return value
- Creates a collapsible hierarchy in log viewers with automatic timing

### 2. Logging IS Comments, Comments ARE Logging

**Do not write comments. Write log statements instead.**

The log serves as both runtime documentation AND debugging information. Comments are invisible in
production; logs are not.

```csharp
// BAD - Comment invisible in production
// Validate the order before processing
if (!order.IsValid)
    return null;

// GOOD - Log visible in production, serves as documentation
logger.Debug("Validating order before processing");
if (!order.IsValid)
{
    logger.Debug("Order validation failed");
    return null;
}
```

### 3. Log Calculated and Fetched Values

When you calculate a value or fetch it from somewhere (database, API, config), log it.

```csharp
var discount = CalculateDiscount(customer);
logger.Inspect("discount", discount);

var user = await _repository.GetUserAsync(userId);
logger.Inspect("user.Email", user?.Email ?? "not found");
```

### 4. Use LogObject for Complex Types

When fetching objects from a database or receiving complex DTOs, use `LogObject` to capture the full state.

```csharp
var order = await _db.Orders.FindAsync(orderId);
logger.LogObject(order);

var response = await _client.GetAsync<ApiResponse>(url);
logger.LogObject(response);
```

`LogObject` uses `JsonObjectSerializer` which:
- **Catches exceptions from property getters** — Some objects (e.g., MemoryStream) have properties that throw when accessed. The serializer catches these and returns defaults.
- **Respects [Sensitive] attribute** — Properties marked with `[Sensitive]` are masked.

### 5. Mark Sensitive Data with [Sensitive]

Never log passwords, API keys, tokens, or PII. Mark sensitive properties with the `[Sensitive]` attribute:

```csharp
public class UserCredentials
{
    public string Username { get; set; }

    [Sensitive]
    public string Password { get; set; }

    [Sensitive]
    public string ApiKey { get; set; }
}

// Logs: { "Username": "jsmith", "Password": "Sensitive - HasValue: true", "ApiKey": "Sensitive - HasValue: true" }
logger.LogObject(credentials);
```

### 6. Provide Context for Problem-Solving

Include relevant IDs, states, and values.

```csharp
logger.Debug("Processing payment for Order {OrderId}, Amount {Amount}, Customer {CustomerId}",
    order.Id, order.Total, order.CustomerId);
```

### 7. Exception Context is Critical

```csharp
catch (Exception ex)
{
    logger.Error(ex, "Failed to process order {OrderId} for customer {CustomerId} with amount {Amount}",
        orderId, customerId, amount);
    throw;
}
```

### Summary

| Principle | Practice |
|-----------|----------|
| Start methods | `using var _ = _logger.EnterMethod();` |
| Get a logger | Inject `ILoggerSource loggers`; `_logger = loggers.CreateLogger<MyType>();` |
| Replace comments | `logger.Debug("Explanation of what's happening");` |
| Log values | `logger.Inspect("x", x);` |
| Log complex objects | `logger.LogObject(dto);` |
| Mark sensitive data | `[Sensitive]` attribute on properties |
| Provide context | Include IDs, states, relevant values |
| Exception handling | Include context: IDs, values, state at time of failure |

---

## ILoggerSource / SerilogLoggerSource

`ILoggerSource` is the single seam an application injects to obtain category-scoped loggers,
independent of any provider:

```csharp
public interface ILoggerSource
{
    ILogger CreateLogger<T>();          // category = concise full name of T
    ILogger CreateLogger(Type source);  // category = concise full name of the type
    ILogger CreateLogger(string category);
}
```

All three return `Serilog.ILogger`. The method is named `CreateLogger`, not `For`, to satisfy analyzer
rule CA1716.

- **`SerilogLoggerSource`** — the canonical-Serilog default. Each logger is the root logger with its
  `SourceContext` set to the category (`root.ForContext(SourceContext, category)`). The logging API
  works identically; `IsEnabled`-based guards fall back to the configured Serilog minimum level.
- **A provider supplies a smarter source.** `Pondhawk.Logging.Watch` provides a switch-aware
  `WatchLoggerSource`: because the API gates on `ILogger.IsEnabled`, loggers from that source skip
  serialization for switch-dropped categories with no change to calling code.
- **An app can implement its own `ILoggerSource`** and drop the Watch package entirely — handlers that
  depend only on `ILoggerSource`/`ILogger` are unchanged.

Prefer injecting an `ILoggerSource` over `Log.ForContext<T>()`; the latter is the fallback and is not
switch-aware.

---

## Extension Method Reference

### Method Tracing

```csharp
using var scope = logger.EnterMethod();   // extension on Serilog ILogger
// Logs entry with Nesting=+1, exit with Nesting=-1 and timing
```

### Logger Creation

```csharp
// Idiomatic: inject ILoggerSource; sets SourceContext to the concise type name.
private readonly ILogger _logger;
public MyType(ILoggerSource loggers) => _logger = loggers.CreateLogger<MyType>();

// Fallback: standard Serilog acquisition.
private readonly ILogger _logger = Log.ForContext<MyType>();
```

### Typed Payloads

```csharp
logger.LogObject(dto);              // Serializes to JSON
logger.LogObject("Title", dto);     // With a custom title
logger.LogJson("Title", jsonStr);   // Raw JSON with highlighting
logger.LogSql("Query", sqlStr);     // SQL syntax highlighting
logger.LogXml("Config", xmlStr);    // XML syntax highlighting
logger.LogYaml("Data", yamlStr);    // YAML syntax highlighting
logger.LogText("Output", textStr);  // Plain text
logger.Inspect("name", value);      // Logs "name = value" at Debug
```

---

## Property-Name Contract

`LogPropertyNames` (public) defines the well-known Serilog property names the API writes and sinks read.
All are prefixed `Pondhawk.`:

- `Pondhawk.Nesting` — method-tracing depth (+1 enter, -1 exit)
- `Pondhawk.PayloadType` — int value of the `PayloadType` enum
- `Pondhawk.PayloadContent` — serialized payload string
- `Pondhawk.CorrelationId` — correlation identifier
- `pondhawk.correlation` — `Activity` baggage key used to flow the correlation id

## Project Structure

```
src/Pondhawk.Logging/
  SerilogExtensions.cs                  # EnterMethod, Inspect, LogObject, LogJson, etc.
  MethodLogger.cs                       # ILogger wrapper behind EnterMethod
  ILoggerSource.cs                      # Logger-acquisition abstraction
  SerilogLoggerSource.cs                # Canonical-Serilog default ILoggerSource
  LogPropertyNames.cs                   # Public Pondhawk.* property-name contract
  PayloadType.cs                        # None, Json, Sql, Xml, Text, Yaml
  SensitiveAttribute.cs                 # [Sensitive] for masking properties
  CorrelationManager.cs                 # Activity-based correlation ID management
  GlobalUsings.cs                       # Shared usings for the project

  Serializers/
    IObjectSerializer.cs                # Object to payload abstraction
    JsonObjectSerializer.cs             # System.Text.Json with safe property access + [Sensitive] masking
    LoggingJsonTypeInfoResolver.cs      # Safe getter wrapping + [Sensitive] handling
    AttributeJsonConverter.cs           # Attribute → { "Name": "..." }
    TypeJsonConverter.cs                # Type → { "Name": "..." }

  Utilities/
    TypeExtensions.cs                   # public GetConciseName/GetConciseFullName with caching
```

`TypeExtensions` (`GetConciseName` / `GetConciseFullName`) is **public** in this package.

## Common Mistakes

- Don't use string interpolation: `$"User {user}"` allocates even when the level is disabled
- Do use structured logging: `"User {UserId}", userId`
- Do use `EnterMethod()` for method-level tracing
- Do use appropriate `PayloadType` for syntax highlighting
- Prefer an injected `ILoggerSource` over `Log.ForContext<T>()`
