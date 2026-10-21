# Pondhawk.Logging.Migration

A Roslyn analyzer with code fixes that moves an application off **Fabrica.Watch** and onto
`Microsoft.Extensions.Logging` and the [`Pondhawk.Logging`](../Pondhawk.Logging/README.md) API. It is a
development-only reference: nothing ships with the application.

```xml
<PackageReference Include="Pondhawk.Logging.Migration" PrivateAssets="all" />
```

## Running it

Keep Fabrica.Watch referenced while the migration runs — the rules recognise its calls by their symbols — and
add `Pondhawk.Logging`. Then apply every fix across the solution:

```bash
dotnet format analyzers <solution> --severity info --diagnostics \
  PHLM001 PHLM002 PHLM003 PHLM004 PHLM005 PHLM006 PHLM007 PHLM008 PHLM009 PHLM011 \
  PHLM012 PHLM013 PHLM014 PHLM015 PHLM016 PHLM017 PHLM020
```

Run it until it changes nothing — three or four runs. Calls on a logger are rewritten first, then where the
logger comes from, then the using swap (PHLM015), each only once the one before is done in that file. Every rule
is Info by default: raise them in `.editorconfig` to stop new Fabrica.Watch calls appearing mid-migration. The
whole path, including what is left for you, is in
[MIGRATING-FROM-FABRICA-WATCH.md](../../MIGRATING-FROM-FABRICA-WATCH.md).

**What a fix cannot settle, it marks.** Where a call cannot be converted cleanly, the fix still writes code
that compiles and puts a comment above the statement:

```csharp
// TODO(Fabrica.Watch migration): the template could not be turned into a message template, so it is formatted as before; give it named holes
logger.LogDebug("{Message}", string.Format(format, count));
```

Search for `TODO(Fabrica.Watch migration)` when the run is done.

## Rules

| Rule | Finds | Becomes |
|---|---|---|
| PHLM001 | `Trace/Debug/Info/Warning/Error(message)`, `(exception, message)`, or a template overload with no arguments | `LogTrace/LogDebug/LogInformation/LogWarning/LogError`. A constant message stays the template; any other is the value of a `{Message}` (a string) or `{Value}` hole; an exception without a message logs its own. |
| PHLM002 | A positional template with arguments: `Debug("Placed {0} for {1:N0}", id, order.Total)` (or `DebugFormat(…)`, as some copies name it) | `LogDebug("Placed {Id} for {OrderTotal:N0}", id, order.Total)`. Holes are named from the arguments (last two name segments, looking through `?.`, `!`, a cast and the left of `??`; `x.GetType().FullName` → `XType`; after a call, the member that follows — `ex.GetBaseException().Message` → `Message`; a clash takes another segment, then a number; `Arg0` for an argument with no name); alignment and format kept; a value used twice is passed twice. A template that is not a constant, an explicit array, or a format that cannot be read is formatted with `string.Format` as before — TODO. |
| PHLM003 | An interpolated or concatenated message | A message template and its values: `$"Placed {count}"` → `"Placed {Count}", count` |
| PHLM004 | A `Func<string>` message: `Debug(() => …)` | `if (logger.IsEnabled(LogLevel.Debug)) logger.LogDebug(…)`, so it is still built only when the level is on |
| PHLM005 | `IsTraceEnabled` … `IsErrorEnabled` | `IsEnabled(LogLevel.Trace)` … `IsEnabled(LogLevel.Error)` (`Info` → `Information`) |
| PHLM006 | `LeaveMethod()`, a `logger.EnterMethod();` a `LeaveMethod` closes, and `using` on a logger that is not a method trace | `LeaveMethod` goes; the `EnterMethod` becomes a `using` declaration; a `try/finally` that only closed the trace goes with it (a `catch` stays). An `EnterMethod` nothing here closes is left for whoever disposes the logger. A `using` on a plain logger, or a `logger.Dispose()`, goes — a Microsoft.Extensions.Logging logger is not disposable. A `LeaveMethod` with no `EnterMethod` to pair with — TODO. |
| PHLM007 | `EnterScope(name)` … `LeaveScope(name)` in one block | `using (logger.EnterMethod(name)) { … }` around what they bracketed. When a local declared inside is used after, a `using var scope = …` instead — TODO. |
| PHLM008 | `ErrorWithContext` or `WarningWithContext` without a string message; any other level's `…WithContext`, such as the `InfoWithContext` some copies add | `ErrorWithContext(ex, context, ex.Message)` (or `$"{message}"`), the same for `WarningWithContext`; `LogWithContext(LogLevel.X, [ex,] context, message)` for the other levels — always one call |
| PHLM009 | `Inspect` with a name that is not a string | `Inspect("42", value)` or `Inspect($"{name}", value)` |
| PHLM010 | `LogObject(title, value)` | **Report only.** Logs at Trace in Pondhawk.Logging, Debug in Fabrica.Watch; carries over with PHLM015 |
| PHLM011 | `LogJson/LogXml(title, text, pretty)` | The `pretty` flag is dropped |
| PHLM012 | `WatchFactoryLocator.Factory.GetLogger(category)` / `GetLogger<T>()` / `GetLogger(Type)` | `LoggingFactoryLocator.GetFactory().CreateLogger(…)` — the `retroOn` flag is dropped |
| PHLM013 | `EnterMethod<T>()` with type arguments or a named argument; `EnterMethodSlim(category)` | `this.EnterMethod()`; `LoggingFactoryLocator.GetFactory().CreateLogger(category).EnterMethod()` |
| PHLM014 | `Fabrica.Watch.SensitiveAttribute`, `Fabrica.Watch.Sink.PayloadType` written in full | `Pondhawk.Logging`'s |
| PHLM015 | `using Fabrica.Watch;` (and `.Sink`, …) once nothing in the file needs it | `using Microsoft.Extensions.Logging;` and `using Pondhawk.Logging;` — each only if the file uses it |
| PHLM016 | `Fabrica.Watch.ILogger` written in full | `Microsoft.Extensions.Logging.ILogger` |
| PHLM017 | Fabrica.Watch's `ILogger` in the signature of a member that is not private: `protected ILogger EnterMethod()`, `public ILogger Logger { get; }`, `internal void Write(ILogger logger)` | `MethodLogger` for a method that enters a trace (named `EnterMethod…`, or returning an `EnterMethod` call), Microsoft.Extensions.Logging's `ILogger` otherwise. A type the member hands out (return, property, field, `out`) is changed only once every file using the member has no Fabrica.Watch left; until then the fix is not offered. |
| PHLM020 | `var ev = logger.CreateEvent(…); logger.LogEvent(ev);` | The one call that says the same: `LogJson/LogXml/LogSql/LogYaml/LogText(level, title, payload)`, `LogObject(level, title, value)`, `ErrorWithContext`/`WarningWithContext(ex, context, title)` (or `LogWithContext(level, ex, context, title)` below Warning), or `LogX(title)`. An event changed or passed on before it is logged, or whose level or payload type is not a constant, is reported only. |
| PHLM021 | `WatchFactoryBuilder`, its `Use…` extensions, `WatchFactoryLocator.SetFactory` | **Report only.** Configure `AddWatch` / `AddCloudWatch` / `AddJournaldConsole`; `LoggingFactoryLocator.SetFactory` once |
| PHLM022 | The Fabrica.Watch.Bridges provider | **Report only.** Remove it — Microsoft.Extensions.Logging is the API now |
| PHLM023 | `GetCurrentScope`, `SetCurrentScope`, a logger's `Category` | **Report only.** No equivalent on `ILogger` |
| PHLM024 | Any other Fabrica.Watch use — an API a copy of Fabrica.Watch added, a sink type | **Report only.** Migrate by hand |

Many Fabrica.Watch names mean the same thing under the new usings, so they need no rewrite of their own and
carry over with PHLM015: `ILogger`, `[Sensitive]`, `PayloadType` and its members, `this.GetLogger()`,
`this.EnterMethod()`, `LogSql`, `LogYaml`, `LogObject(title, value)`, `LogJson`/`LogXml(title, text)`,
`Inspect` with a string name, and `ErrorWithContext` and `WarningWithContext` with a string message — with an
exception, or the `(context, message)` overloads without one that some copies add.

The report-only rules keep a file's Fabrica.Watch using until they are dealt with by hand. So does a file that
hands out Fabrica.Watch's `ILogger` through a member that is not private — a base class's
`protected ILogger EnterMethod()` — until PHLM017 has retyped those members: swapping it early would change that
type under its callers before their calls were rewritten.

## Worth knowing

- **`PayloadType` keeps its member names, not its numbers**: Fabrica.Watch has `Xml = 2, Sql = 3`,
  Pondhawk.Logging `Sql = 2, Xml = 3`. Names carry over; a value converted through an `int` does not.
- **A `global using Fabrica.Watch;`** is not swapped: replace it by hand with the two global usings once the
  rest is done.
- **Fabrica.Watch's own source**, if it is vendored into the solution, is never reported.
- **A constant template held in a field** is converted into a literal at the call.
- **A Fabrica.Watch call inside an interpolation hole** is rewritten on the next run, not in the same one.
