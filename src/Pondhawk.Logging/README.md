# Pondhawk.Logging

The structured logging API for Pondhawk: method tracing, object and typed-payload logging, and
`[Sensitive]` masking, provided as extensions on the standard `Microsoft.Extensions.Logging.ILogger`. It
adds no logger type of its own — application code logs through the standard `ILogger`, so an app can drop
`Pondhawk.Logging` and fall back to plain `Microsoft.Extensions.Logging` with a configuration change and
no code edits. It has **no sink or transport** — provider packages (e.g.
[`Pondhawk.Logging.Watch`](../Pondhawk.Logging.Watch/README.md)) supply delivery.

Fully standalone — no dependency on other Pondhawk packages. Targets `net8.0`.

## The Logging API

Extensions on `ILogger` (`using Microsoft.Extensions.Logging;` for the logger, `using Pondhawk.Logging;`
for these):

- **`ILogger.EnterMethod()`** — disposable method-tracing scope with automatic entry/exit logging and elapsed time
- **`ILogger.Inspect(name, value)`** — logs a name/value pair as `"{Name} = {Value}"` at Debug level
- **`ILogger.LogObject(value)`** / **`LogObject(title, value)`** — serializes an object to a JSON payload
- **`ILogger.LogJson/LogSql/LogXml/LogYaml/LogText(title, content)`** — typed payload logging with syntax-highlighting hints
- **`[Sensitive]`** — attribute that masks a property when an object is serialized (`"Sensitive - HasValue: true"`)

Each method guards on `ILogger.IsEnabled` first, so a disabled (e.g. switch-dropped) category pays no
serialization cost.

Also included: `LogPropertyNames` (the public `Pondhawk.*` log-state property-name contract that sinks
read), the serializers (`JsonObjectSerializer` and friends), the `PayloadType` enum, `CorrelationManager`,
and public `TypeExtensions` (`GetConciseName` / `GetConciseFullName`).

## Acquiring loggers

Loggers come from the standard `ILoggerFactory` — there is no proprietary acquisition type. Inject
`ILoggerFactory` and create a category logger by type or name:

```csharp
ILogger logger = loggerFactory.CreateLogger<OrderService>();   // or CreateLogger("My.Category")
```

The returned `ILogger` is the standard `Microsoft.Extensions.Logging.ILogger`. Because the whole API
gates on `ILogger.IsEnabled`, a provider that makes `IsEnabled` switch-aware (as `Pondhawk.Logging.Watch`
does, via a level filter) makes the entire API skip work for switch-dropped categories — with no change
to calling code.

## Usage

Inject `ILoggerFactory`, create a category logger, and call the API on it:

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

        _logger.LogDebug("Loading order {OrderId}", orderId);
        var order = await _repository.GetOrderAsync(orderId);
        _logger.LogObject(order);

        return order;
    }
}
```

## Documentation

See [CLAUDE.md](CLAUDE.md) for the full logging guide (conventions and the extension-method reference).
