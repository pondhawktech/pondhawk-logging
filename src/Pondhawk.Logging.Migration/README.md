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
dotnet format analyzers <solution> --diagnostics PHLM001 PHLM005 PHLM012 PHLM013 PHLM014 PHLM015 PHLM016 --severity info
```

Run it until it changes nothing. The rewrites come first; the using swap (PHLM015) is reported once a file has
nothing left that needs Fabrica.Watch, and replaces its usings. Every rule is Info by default: raise them in
`.editorconfig` to stop new Fabrica.Watch calls appearing mid-migration.

## Rules

| Rule | Finds | Becomes |
|---|---|---|
| PHLM001 | `Trace/Debug/Info/Warning/Error(message)`, `(exception, message)`, or a template overload with no arguments | `LogTrace/LogDebug/LogInformation/LogWarning/LogError`. A constant message stays the template; any other is the value of a `{Message}` (a string) or `{Value}` hole; an exception without a message logs its own. |
| PHLM005 | `IsTraceEnabled` … `IsErrorEnabled` | `IsEnabled(LogLevel.Trace)` … `IsEnabled(LogLevel.Error)` (`Info` → `Information`) |
| PHLM012 | `WatchFactoryLocator.Factory.GetLogger(category)` / `GetLogger<T>()` / `GetLogger(Type)` | `LoggingFactoryLocator.GetFactory().CreateLogger(…)` — the `retroOn` flag is dropped |
| PHLM013 | `EnterMethod<T>()` with type arguments or a named argument; `EnterMethodSlim(category)` | `this.EnterMethod()`; `LoggingFactoryLocator.GetFactory().CreateLogger(category).EnterMethod()` |
| PHLM014 | `Fabrica.Watch.SensitiveAttribute`, `Fabrica.Watch.Sink.PayloadType` written in full | `Pondhawk.Logging`'s |
| PHLM015 | `using Fabrica.Watch;` (and `.Sink`, …) once nothing in the file needs it | `using Microsoft.Extensions.Logging;` and `using Pondhawk.Logging;` — each only if the file uses it |
| PHLM016 | `Fabrica.Watch.ILogger` written in full | `Microsoft.Extensions.Logging.ILogger` |

Many Fabrica.Watch names mean the same thing under the new usings, so they need no rewrite of their own and
carry over with PHLM015: `ILogger`, `[Sensitive]`, `PayloadType` and its members, `this.GetLogger()`,
`this.EnterMethod()`, `LogSql`, `LogYaml`, `LogObject(title, value)`, `LogJson`/`LogXml(title, text)` and
`Inspect` with a string name.

Not yet rewritten — they keep a file's Fabrica.Watch using until they are: templates with arguments,
interpolated and concatenated messages, `Func<string>` messages, `LeaveMethod`, `EnterScope`/`LeaveScope`,
`ErrorWithContext`/`WarningWithContext`, `Inspect` with a non-string name, the `pretty` flag on
`LogJson`/`LogXml`, and the report-only cases (`CreateEvent`, bootstrap, the MEL bridge, scopes).

## Worth knowing

- **`PayloadType` keeps its member names, not its numbers**: Fabrica.Watch has `Xml = 2, Sql = 3`,
  Pondhawk.Logging `Sql = 2, Xml = 3`. Names carry over; a value converted through an `int` does not.
- **A `global using Fabrica.Watch;`** is not swapped: replace it by hand with the two global usings once the
  rest is done.
- **Fabrica.Watch's own source**, if it is vendored into the solution, is never reported.
