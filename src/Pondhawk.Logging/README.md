# Pondhawk.Logging

The Serilog-based structured logging API and the `ILoggerSource` acquisition abstraction. It provides
method tracing, object and typed-payload logging, and `[Sensitive]` masking as extensions on Serilog's
`ILogger`. It is **Serilog-only, with no sink or transport** — provider packages (e.g.
[`Pondhawk.Logging.Watch`](../Pondhawk.Logging.Watch/README.md)) build on it, and an app can implement
its own `ILoggerSource` to drop Watch entirely with handlers unchanged.

Fully standalone — no dependency on Pondhawk.Core. Targets `net10.0`.

## The Logging API

Extensions on `Serilog.ILogger` (`using Pondhawk.Logging;`):

- **`ILogger.EnterMethod()`** — disposable method-tracing scope with automatic entry/exit logging and elapsed time
- **`ILogger.Inspect(name, value)`** — logs a name/value pair as `"{Name} = {Value}"` at Debug level
- **`ILogger.LogObject(value)`** / **`LogObject(title, value)`** — serializes an object to a JSON payload
- **`ILogger.LogJson/LogSql/LogXml/LogYaml/LogText(title, content)`** — typed payload logging with syntax-highlighting hints
- **`[Sensitive]`** — attribute that masks a property when an object is serialized (`"Sensitive - HasValue: true"`)

Also included: `LogPropertyNames` (the public, neutralized `Pondhawk.*` property-name contract that
sinks read), the serializers (`JsonObjectSerializer` and friends), the `PayloadType` enum,
`CorrelationManager`, and public `TypeExtensions` (`GetConciseName` / `GetConciseFullName`).

## ILoggerSource

`ILoggerSource` is the single seam an application injects to obtain category-scoped loggers,
independent of which provider (if any) is wired underneath:

```csharp
public interface ILoggerSource
{
    ILogger CreateLogger<T>();
    ILogger CreateLogger(Type source);
    ILogger CreateLogger(string category);
}
```

All three return `Serilog.ILogger`. (The method is named `CreateLogger`, not `For`, to satisfy analyzer
rule CA1716.)

`SerilogLoggerSource` is the canonical-Serilog default — each logger is the root logger with its
`SourceContext` set to the requested category (`root.ForContext(SourceContext, category)`). Use it when
no smarter provider is configured; the logging API works identically and `IsEnabled`-based guards fall
back to the configured Serilog minimum level. A provider such as `Pondhawk.Logging.Watch` supplies a
switch-aware source (`WatchLoggerSource`) that makes the API skip serialization for switch-dropped
categories — with no change to calling code.

## Usage

Inject an `ILoggerSource`, create a category logger, and call the API on it:

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

        _logger.Debug("Loading order {OrderId}", orderId);
        var order = await _repository.GetOrderAsync(orderId);
        _logger.LogObject(order);

        return order;
    }
}
```

`Log.ForContext<T>()` still works as a fallback for acquiring a logger, but injecting an
`ILoggerSource` is the idiomatic pattern and is what lets a provider make the API switch-aware.

## Documentation

See [CLAUDE.md](CLAUDE.md) for the full logging guide (conventions, extension-method reference, and
`ILoggerSource` details).
