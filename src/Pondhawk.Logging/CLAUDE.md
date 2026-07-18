# Pondhawk.Logging - AI Development Guide

## Overview

Pondhawk.Logging is the **structured logging API** for Pondhawk, built on
`Microsoft.Extensions.Logging`. It provides method tracing, object serialization, typed payloads, and
`[Sensitive]` masking as extensions on the standard `ILogger`. It adds no logger type of its own — code
logs through the standard `ILogger`, so an app can drop this package and fall back to plain
`Microsoft.Extensions.Logging` with a configuration change and no code edits. It has **no sink or
transport** — provider packages such as [`Pondhawk.Logging.Watch`](../Pondhawk.Logging.Watch/CLAUDE.md)
build on it.

Targets `net8.0` (single target — no conditional compilation). Fully standalone — no dependency on other
Pondhawk packages. Namespace: `Pondhawk.Logging`.

---

## Logging Guidelines

**Logging is the primary debugging tool.** You cannot attach a debugger in production, but you can
always read logs. Well-structured logging tells you exactly what happened and why.

### 1. Start Methods with EnterMethod

Most methods should begin with `EnterMethod()`. Only the simplest methods (one-liners, trivial getters)
skip this.

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

- Obtain a logger by injecting an `ILoggerFactory` and calling `CreateLogger<T>()`; the category is the concise type name.
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
logger.LogDebug("Validating order before processing");
if (!order.IsValid)
{
    logger.LogDebug("Order validation failed");
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
logger.LogDebug("Processing payment for Order {OrderId}, Amount {Amount}, Customer {CustomerId}",
    order.Id, order.Total, order.CustomerId);
```

### 7. Exception Context is Critical

```csharp
catch (Exception ex)
{
    logger.LogError(ex, "Failed to process order {OrderId} for customer {CustomerId} with amount {Amount}",
        orderId, customerId, amount);
    throw;
}
```

### Summary

| Principle | Practice |
|-----------|----------|
| Start methods | `using var _ = _logger.EnterMethod();` |
| Get a logger | Inject `ILoggerFactory loggers`; `_logger = loggers.CreateLogger<MyType>();` |
| Replace comments | `logger.LogDebug("Explanation of what's happening");` |
| Log values | `logger.Inspect("x", x);` |
| Log complex objects | `logger.LogObject(dto);` |
| Mark sensitive data | `[Sensitive]` attribute on properties |
| Provide context | Include IDs, states, relevant values |
| Exception handling | Include context: IDs, values, state at time of failure |

---

## Acquiring loggers

Loggers come from the standard `ILoggerFactory` — there is no proprietary acquisition type. Inject
`ILoggerFactory` and create a category logger by type or name:

```csharp
private readonly ILogger _logger;
public MyType(ILoggerFactory loggers) => _logger = loggers.CreateLogger<MyType>();
// or loggers.CreateLogger("My.Category")
```

The returned `ILogger` is the standard `Microsoft.Extensions.Logging.ILogger`. Because the whole API
gates on `ILogger.IsEnabled`, a provider that makes `IsEnabled` switch-aware makes the entire API skip
work for switch-dropped categories with no change to calling code — `Pondhawk.Logging.Watch` does this by
registering a level filter driven by the Watch server's switch table.

---

## Extension Method Reference

### Method Tracing

```csharp
using var scope = logger.EnterMethod();   // extension on ILogger
// Logs entry with Nesting=+1, exit with Nesting=-1 and timing
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

`LogPropertyNames` (public) defines the well-known log-state property names the API attaches and sinks
read. All are prefixed `Pondhawk.`:

- `Pondhawk.Nesting` — method-tracing depth (+1 enter, -1 exit)
- `Pondhawk.PayloadType` — int value of the `PayloadType` enum
- `Pondhawk.PayloadContent` — serialized payload string
- `Pondhawk.CorrelationId` — correlation identifier
- `pondhawk.correlation` — `Activity` baggage key used to flow the correlation id

## Project Structure

```
src/Pondhawk.Logging/
  LoggingExtensions.cs                  # EnterMethod, Inspect, LogObject, LogJson, etc.
  MethodLogger.cs                       # ILogger decorator behind EnterMethod
  LogState.cs                           # log state the API attaches (title + control properties)
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
- Acquire loggers from an injected `ILoggerFactory`
