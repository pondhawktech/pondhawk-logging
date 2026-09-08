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
- **`ILogger.LogObject(value)`** / **`LogObject(title, value)`** — serializes an object to a JSON payload at Trace
- **`ILogger.LogJson/LogSql/LogXml/LogYaml/LogText(title, content)`** — typed payload logging with syntax-highlighting hints, at Debug
- **A `LogLevel` overload of every payload method** — `LogJson(LogLevel.Error, title, json)`, `LogObject(LogLevel.Error, title, value)`, … The payload that matters most is usually the one explaining a failure, and it has to ride on the event that reports the failure rather than on a low-level event a production level drops
- **`ILogger.ErrorWithContext(cause, context, message)`** — logs an error carrying both the exception and a serialized context object, keeping the surrounding state attached to the failure it explains instead of flattened into the message
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

## Standing logging up in tests

`LoggingFactoryLocator.SetFactory` may be called only once per process — a process has one logging
configuration, established at startup. A test assembly is not one application, though, and the natural
per-fixture setup (NUnit's `[OneTimeSetUp]`, xUnit's per-class lifetime) runs once per *fixture*, so
every fixture after the first would throw.

**Stand logging up once for the whole assembly.** In NUnit that is an assembly-level `[SetUpFixture]`:

```csharp
[SetUpFixture]                      // no namespace declaration: applies to the whole assembly
public class LoggingSetup
{
    private static ILoggerFactory _factory;

    [OneTimeSetUp]
    public void SetUp()
    {
        _factory = LoggerFactory.Create(b => b.AddWatch("http://localhost:11000", "Tests"));
        LoggingFactoryLocator.SetFactory(_factory);
    }

    [OneTimeTearDown]
    public void TearDown() => _factory?.Dispose();
}
```

The equivalent in xUnit is an assembly fixture. Either way the factory is built once and disposed once —
which matters beyond the locator: a per-fixture teardown that disposes the factory tears down logging
that other fixtures are still using.

Where a test genuinely needs to swap the factory, `LoggingFactoryLocator.ResetForTesting()` clears the
locator so the next `SetFactory` succeeds.

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
