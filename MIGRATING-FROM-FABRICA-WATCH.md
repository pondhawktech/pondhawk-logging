# Migrating from Fabrica.Watch

How to move an application off **Fabrica.Watch** and onto `Microsoft.Extensions.Logging` with the
Pondhawk.Logging API — what the analyzer does for you, what is left for you, and what behaves differently
afterwards. The end state is plain `Microsoft.Extensions.Logging` code: no compatibility layer, no Fabrica.Watch
reference.

## The order of work

1. **Reference the new packages, keep Fabrica.Watch.** The analyzer recognises Fabrica.Watch calls by their
   symbols, so Fabrica.Watch must still compile.

   ```xml
   <PackageReference Include="Pondhawk.Logging" />
   <PackageReference Include="Pondhawk.Logging.Migration" PrivateAssets="all" />
   ```

2. **Run the analyzer's fixes until they change nothing** (below). This is the mechanical part, and its commit
   can be reviewed by rule rather than by line.
3. **Change where loggers come from, by hand** — base classes and helpers that hand out loggers, bootstrap,
   events built by hand, anything the analyzer reported but could not rewrite. Then run the fixes once more:
   what you changed lets the last files' usings go.
4. **Build, fix what remains, search for the TODOs, then remove Fabrica.Watch and the analyzer.**

Steps 2 and 3 belong in one pull request: in between, files whose loggers still come from Fabrica.Watch do not
compile.

## Running the analyzer

```bash
dotnet format analyzers <solution> --severity info --diagnostics \
  PHLM001 PHLM002 PHLM003 PHLM004 PHLM005 PHLM006 PHLM007 PHLM008 PHLM009 PHLM011 \
  PHLM012 PHLM013 PHLM014 PHLM015 PHLM016 PHLM017 PHLM020
```

Run it again until a run changes no files — three or four runs. The order matters and the rules take care of
it across runs: calls on a logger are rewritten first, then where the logger comes from (`WatchFactoryLocator`,
`EnterMethodSlim`, a fully qualified `ILogger`, a member handing a logger to other files), and last the file's
usings (PHLM015), which turn every name that
means the same thing — `ILogger`, `[Sensitive]`, `PayloadType`, `this.GetLogger()`, `this.EnterMethod()`,
`LogSql`, `LogObject` … — into the new one. The rules are listed in the
[package README](src/Pondhawk.Logging.Migration/README.md).

**What a fix cannot settle, it marks.** It still writes code that compiles, with a comment above the statement:

```csharp
// TODO(Fabrica.Watch migration): the template could not be turned into a message template, so it is formatted as before; give it named holes
logger.LogDebug("{Message}", string.Format(format, count));
```

Search for `TODO(Fabrica.Watch migration)` when the runs are done.

**Why a file keeps `using Fabrica.Watch;`** after the runs — each is your step 3:

- It still has something only reported: an event changed or passed on before it is logged (PHLM020), set-up (PHLM021), the bridge
  (PHLM022), a logger's scope or category (PHLM023), or a Fabrica.Watch API no rule knows (PHLM024 — APIs a copy
  of Fabrica.Watch added, sink types). Run the analyzer with `--verify-no-changes --report <dir>` and these ids to
  list them.
- It **hands out loggers** that PHLM017 has not retyped yet: a member that is not private has Fabrica.Watch's
  `ILogger` in its signature — a base class's `protected ILogger EnterMethod()`, a helper returning a logger.
  PHLM017 retypes it (to `MethodLogger` for a method that enters a trace, to Microsoft.Extensions.Logging's
  `ILogger` otherwise), but a type the member hands out only once every file using the member has no Fabrica.Watch
  left: changed earlier, it would change that type under their calls before those were rewritten. Until then the
  diagnostic shows with no fix, and its message names the files in the same project it is waiting for (a file in
  another project — a test project subclassing a base class, say — holds it back too, but is not named). A file
  using the member that still has something only reported holds it back — deal with that and run again.

## Where loggers come from

An application usually hands out loggers through a few helpers: a base class, an extension on its own context
type, a factory. These are the heart of step 3. PHLM017 changes their signatures once their callers are migrated;
what a helper does inside — building a logger with Fabrica.Watch's factory — is yours. Change it **after** the
analyzer runs, never before — a helper already returning a Microsoft.Extensions.Logging logger hides its callers'
Fabrica.Watch calls from the rules.

- A helper that **returns a logger** creates one from the factory:

  ```csharp
  // was: WatchFactoryLocator.Factory.GetLogger(ref request) with tenant, subject and correlation id
  public static ILogger GetLogger(this ICorrelation correlation, string category) =>
      LoggingFactoryLocator.GetFactory().CreateLogger(category);
  ```

  Prefer injecting `ILoggerFactory` or `ILogger<T>` where the code allows it; `LoggingFactoryLocator` is for code
  that cannot take a dependency.

- A helper that **enters a method trace and returns the logger** for its caller to dispose returns the
  `MethodLogger` instead — it is an `ILogger`, and disposing it ends the trace:

  ```csharp
  // was: var logger = GetLogger(correlation, type); logger.EnterMethod(name); return logger;
  public static MethodLogger EnterMethod(this ICorrelation correlation, Type type, [CallerMemberName] string name = "") =>
      GetLogger(correlation, type).EnterMethod(name);
  ```

  Callers keep `using var logger = correlation.EnterMethod<T>();` unchanged.

- **A plain logger is not disposable.** Every Fabrica.Watch logger was; a Microsoft.Extensions.Logging one is not.
  The analyzer removes a `using` or a `Dispose()` on a logger that is not a method trace (PHLM006); only an
  `EnterMethod` has something to close.

## Bootstrap

`WatchFactoryBuilder` and its `Use…` sinks go; the providers are added to the host's logging builder:

```csharp
builder.Logging.ClearProviders();
builder.Logging.AddWatch("https://pwk_…@watch.example.com/MyApp");       // Watch Server
builder.Logging.AddCloudWatch("/myapp", "orders-api");                    // CloudWatch Logs
builder.Logging.AddJournaldConsole();                                     // systemd journal, Warning and above
// builder.Logging.AddJournaldConsole(LogLevel.Information);              // …or from Information, as Fabrica's UseJournal wrote

var app = builder.Build();
LoggingFactoryLocator.SetFactory(app.Services.GetRequiredService<ILoggerFactory>());   // once, after the host is built
```

- Remove the Fabrica.Watch.Bridges `LoggerProvider` (PHLM022): Microsoft.Extensions.Logging is the API now.
- `LoggingFactoryLocator.SetFactory` may be called **once** per process — unlike `WatchFactoryLocator`, where the
  last call won. Test assemblies stand logging up once (below).
- A sink of your own built on Fabrica.Watch's (a CloudWatch sink, a journal sink) is replaced by the provider, not
  migrated. Its tests go with it.

## Correlation, subject and tenant

Fabrica.Watch stamped a correlation id, subject and tenant on each **logger**, from a `LoggerRequest` built when
the logger was made. Pondhawk.Logging providers read them from the **ambient** `IOperationContext`
(Pondhawk.Context) when each event is logged.

- In ASP.NET Core, `Pondhawk.Logging.AspNetCore` sets them for every request — `UseDiagnosticsMonitor` and
  `UseDiagnosticsEnrichment`. Remove Fabrica's request middleware in the same change.
- For background work, begin an operation where the unit of work starts:

  ```csharp
  using (Operation.Begin(correlationId))   // was e.g. WatchCorrelation.Begin(id)
  {
      Ambient<IOperationContext>.Current.SubjectName = "scheduler";
      …
  }
  ```

## Events built by hand

Pondhawk.Logging has no event object: one call says what `CreateEvent` + `LogEvent` said in two. The analyzer
rewrites an event created and logged at once (PHLM020):

```csharp
var ev = logger.CreateEvent(Level.Info, "Appliance Info", PayloadType.Yaml, yaml);   // was
logger.LogEvent(ev);

logger.LogYaml(LogLevel.Information, "Appliance Info", yaml);                         // is
```

A typed payload becomes `LogJson/LogXml/LogSql/LogYaml/LogText(level, title, payload)`, an object
`LogObject(level, title, value)`, an exception with context `ErrorWithContext` or `WarningWithContext` — and a title alone `LogX(title)`.
An event changed or passed on before it is logged, or whose level or payload type is not a constant, is left for
you, with the same methods to reach for.

## The diagnostic probe

Fabrica clients sent `X-Diagnostics-Probe: 1` to raise one request's logging, and any caller could. With
`Pondhawk.Logging.AspNetCore` and the Watch provider, the same header carries a **token issued in Watch Server**:
support issues it for a user (up to 4 hours) or for anyone (up to 1 hour) and the user pastes it into the
client's support setting. A probed request logs at Trace whatever the switches say, request dump included. A
plain `1` is refused — logged once, the request otherwise unaffected. See the
[AspNetCore README](src/Pondhawk.Logging.AspNetCore/README.md#the-diagnostic-probe).

## Tests

- Stand logging up **once per test assembly** — an NUnit `[SetUpFixture]`, an xUnit assembly fixture — since
  `LoggingFactoryLocator.SetFactory` is set-once. `LoggingFactoryLocator.ResetForTesting()` is there for a test
  that must swap factories.
- To assert on what was logged, capture through an `ILoggerProvider` added to the test's factory rather than a
  Fabrica.Watch sink.

## Switches and levels

- Watch Server's switches replace Fabrica.Watch's switch source (a Mongo collection, a local source), and use
  `Microsoft.Extensions.Logging`'s level names: `Trace, Debug, Information, Warning, Error, Critical, None`.
  Fabrica's `Quiet` is `None`.
- **Method entry and exit log at Debug**, as Fabrica.Watch did. **`LogObject` logs at Trace** — Fabrica.Watch used
  Debug — because its payload is expensive to build (PHLM010 points each one out). A category switched to Debug
  no longer shows those objects; pass a level, `LogObject(LogLevel.Debug, title, value)`, where one must show.
- The Watch provider's switch filter is global — it decides for every provider. Give another provider its own
  floor with a provider-scoped filter.
- Retention is Watch Server's, per domain; nothing in the application keeps Trace and Debug for hours and the
  rest for days as Fabrica's Mongo sink did.

## What behaves differently

| Fabrica.Watch | Pondhawk.Logging / Microsoft.Extensions.Logging |
|---|---|
| `Fabrica.Watch.ILogger`, disposable | `Microsoft.Extensions.Logging.ILogger`, not disposable; a `MethodLogger` is |
| `Level { Trace, Debug, Info, Warning, Error, Quiet }` | `LogLevel` — `Info` → `Information`, `Quiet` → `None`; adds `Critical` |
| `Debug(message)`, `Info(message)` … | `LogDebug`, `LogInformation` … |
| `Debug("x {0}", a)` — `string.Format` | `LogDebug("x {A}", a)` — a message template with named holes |
| `Debug(() => …)` | `if (logger.IsEnabled(LogLevel.Debug)) logger.LogDebug(…)` |
| `IsDebugEnabled` | `IsEnabled(LogLevel.Debug)` |
| `EnterMethod()` … `LeaveMethod()` | `using var logger = this.EnterMethod();` — the dispose ends it |
| `EnterScope(name)` … `LeaveScope(name)` | `using (logger.EnterMethod(name)) { … }` |
| `Inspect(name, value)` → `Variable: name = (value)` | `Inspect(name, value)` → `name = value`, at Debug; the name is a string |
| `LogObject(title, value)` at Debug | at Trace; a `LogLevel` overload for any other |
| `LogJson/LogXml(title, text, pretty)` | no `pretty`: the content is logged as given |
| `ErrorWithContext(ex, context, message?)`, `WarningWithContext(…)` | the same method — the message is required |
| `ErrorWithContext(context, message)`, `WarningWithContext(context, message)` (some copies) | the same method, unchanged — still one event |
| `InfoWithContext` and other levels | `LogWithContext(LogLevel.X, [ex,] context, message)` — one event |
| `CreateEvent` + `LogEvent` | a payload method at the event's level, or `ErrorWithContext`/`WarningWithContext` |
| `this.GetLogger()`, `this.EnterMethod<T>()` | `this.GetLogger()`, `this.EnterMethod()` |
| `WatchFactoryLocator.Factory.GetLogger(…)` — last `SetFactory` wins | `LoggingFactoryLocator.GetFactory().CreateLogger(…)` — set once |
| `WatchFactoryBuilder.Use…` | `AddWatch` / `AddCloudWatch` / `AddJournaldConsole` |
| Correlation, subject, tenant on each logger | the ambient `IOperationContext` |
| `Fabrica.Watch.SensitiveAttribute` | `Pondhawk.Logging.SensitiveAttribute` |
| The MEL bridge | not needed |

Also worth knowing:

- **Messages are templates now, not `string.Format`.** A message with braces and no arguments is logged as
  written — it no longer throws.
- **`[Sensitive]` masking runs through System.Text.Json**, not Newtonsoft. A type that relied on Newtonsoft
  attributes (`[JsonIgnore]`, `[JsonProperty]`) to hide members from a logged object must be checked.
- **`PayloadType` keeps its member names, not its numbers** — Fabrica.Watch has `Xml = 2, Sql = 3`,
  Pondhawk.Logging `Sql = 2, Xml = 3`. Convert by name, never through an `int`.

## Copies of Fabrica.Watch that differ

Fabrica.Watch is often vendored and changed. The analyzer recognises it by name, finds arguments by parameter
rather than position, and knows two common changes: template overloads renamed `DebugFormat`, `WarningFormat` …,
and `…WithContext(context, message)` overloads without an exception (at Error and Warning they carry over
unchanged; `InfoWithContext` becomes `LogWithContext(LogLevel.Information, …)`). Anything else such a copy adds is reported
as PHLM024 for you to migrate. Fabrica.Watch's own source, vendored into the solution, is never reported or
rewritten.

A **library vendored alongside Fabrica.Watch** — a `Fabrica.Core` that hands out loggers — is part of the
migration: its logger helpers are step 3, and changing them means its copy diverges from upstream. Decide that
before you start.

## Proven on

**fabrica-one** (2026-10-15): 17 projects, about 600 Fabrica.Watch call sites. The runs converge in four passes,
rewrite 59 files and leave 4 TODOs. With two hand changes after them — `Fabrica.Core`'s logger helpers, and
`WatchCorrelation.Begin` → `Operation.Begin` — every application project compiles. What remains is set-up (72
sites) and the tests of the Fabrica.Watch machinery being removed: its factory, its correlation scope, its own
CloudWatch sink. The trial found and fixed ten analyzer defects, each now a test.
