# Plan: first-class migration from Fabrica.Watch

Status: **PLAN** — 2026-10-15. Nothing implemented yet.

## Why

Applications built on Fabrica.Watch (`Fabrica.Watch`, `.Bridges`, `.Mongo`, `.Realtime`) are moving
to Pondhawk.Logging. Done by hand, each migration repeats the same mechanical rewrite — hundreds to
thousands of level calls, positional templates, manual `LeaveMethod` blocks — and trips over the same
behavioural differences. This plan makes the migration a supported path in this repository, so every
application moving off Fabrica.Watch gets the same tooling and the same result: plain
`Microsoft.Extensions.Logging` code on the Pondhawk.Logging API, with no compatibility layer left
behind.

Five pieces of work:

1. **Method tracing at Debug** — a behaviour change in `Pondhawk.Logging`.
2. **`Pondhawk.Logging.Migration`** — a new, development-only Roslyn analyzer with code fixes that
   rewrites Fabrica.Watch call sites.
3. **Diagnostics readiness** — confirm `Pondhawk.Logging.AspNetCore` can replace Fabrica's request
   diagnostics middleware, and fix what stops it.
4. **Documentation fixes** — places where the docs and the code disagree.
5. **Switch level `None`** — `Pondhawk.Logging.Watch` honours a switch that turns a category off.

Not in scope: Fabrica-style alias methods (`Debug`, `Info`, `Inspect` on `ILogger`) — the earlier
decision against level-name aliases stands, and the analyzer makes them unnecessary; a MongoDB sink
writing the legacy Fabrica event store; a testing package; a hook for hosts to feed their own
correlation id into `IOperationContext`.

---

## 1. Method tracing at Debug (`Pondhawk.Logging`)

**Change.** `ILogger.EnterMethod()` logs its entry event ("Entering X", `Pondhawk.Nesting` +1) and
`MethodLogger.Dispose()` logs its exit event ("Exiting X (n.nnms)", `Pondhawk.Nesting` −1) at
**`LogLevel.Debug`**, always. Today both are `LogLevel.Trace`
(`src/Pondhawk.Logging/LoggingExtensions.cs:39-51`, `src/Pondhawk.Logging/MethodLogger.cs:36-50`).

**Why.** Method flow is the backbone of a Watch trace; it belongs at the level an operator opens to
debug a category. At Trace, a category switched to Debug shows the `LogDebug` breadcrumbs with none of
the method structure that nests them. Fabrica.Watch logged method entry and exit at Debug, so this also
removes the most visible behavioural difference for migrating applications.

**Unchanged.** `LogObject` stays at Trace (`DefaultObjectLevel`); typed payloads stay at Debug.

**Details.**
- Rename `MethodLogger`'s `_tracing` flag (it now records whether the entry event was written).
- Entry and exit are strictly paired: the exit event is written, at Debug, whenever the entry event
  was, with **no second level check at exit** — today `Dispose` re-checks `IsEnabled`, so a switch
  lowered while the method runs leaves the entry's `Pondhawk.Nesting` +1 without its −1. If the entry
  was suppressed, no exit is written.
- **Limit (found while implementing).** That removes only `MethodLogger`'s own check.
  Microsoft.Extensions.Logging applies its filters again on every `Log` call, and a Watch switch is one of
  them, so a switch lowered while the method runs still drops the exit. The API cannot get past the
  pipeline. Decided: accept it. Watch's call tree tolerates an entry without its exit — that one
  correlation's tree sits a level deep, every event still shows
  (`watch-server/docs/plans/2026-10-15-mel-level-names.md` §8).
- `object.EnterMethod()` (`ObjectLoggingExtensions`) inherits the change.
- Update XML docs on `EnterMethod`, `MethodLogger`, and the `LogObject` remarks that compare levels.

**Tests** (`test/Pondhawk.Logging.Tests`): entry and exit logged at Debug when Debug is enabled; neither
logged when only Information is enabled; no exit event when entry was suppressed; exit still logged
when Debug is disabled between entry and dispose; existing
`EnterMethod_WhenTraceDisabled_LogsNoTrace` replaced by its Debug equivalent; `MethodLoggerTests`
updated for the level.

**Docs.** README, `src/Pondhawk.Logging/README.md`, `CLAUDE.md`, `src/Pondhawk.Logging/CLAUDE.md`,
`llms.txt` — every mention of method tracing at Trace.

**Release.** A visible behaviour change for existing adopters: wherever a Watch switch holds a
category at Debug, method entry/exit events now appear (more volume). Bump `Pondhawk.Logging`
`version.json` from `3.0` to `3.1` and call it out in the release notes. Provider packages need no code
change; they accept any version ≥ their reference.

---

## 2. `Pondhawk.Logging.Migration` — Fabrica.Watch migration analyzer

### Shape

- New project `src/Pondhawk.Logging.Migration`, packed as an analyzer (`analyzers/dotnet/cs`),
  consumed as a development-only reference:

  ```xml
  <PackageReference Include="Pondhawk.Logging.Migration" PrivateAssets="all" />
  ```

  No runtime assembly; nothing ships with the application.
- Targets `netstandard2.0` (Roslyn's requirement) — the one project not on `net8.0`. Needs a
  `src/Directory.Build.props` carve-out; keep `TreatWarningsAsErrors` and the Meziantou rules, add the
  Roslyn analyzer-authoring rules (`Microsoft.CodeAnalysis.Analyzers`, release tracking files).
  `Nullable=enable` on `netstandard2.0` needs the nullable attributes, which that target lacks:
  supply them with **PolySharp** (a build-only source generator, `PrivateAssets="all"`, pinned in
  `Directory.Packages.props`), which generates only the missing attributes (and `IsExternalInit`) and
  ships nothing in the package.
- References `Microsoft.CodeAnalysis.CSharp` / `.Workspaces` at a version old enough for current SDKs
  to load (pin in `Directory.Packages.props`).
- **No reference to Fabrica.Watch.** Rules recognise Fabrica.Watch symbols by metadata name
  (`Fabrica.Watch.ILogger`, `Fabrica.Watch.LoggerExtensions`, `Fabrica.Watch.WatchExtensions`,
  `Fabrica.Watch.WatchFactoryLocator`, …), so the analyzer works on any vendored or packaged copy of
  Fabrica.Watch, whatever assembly it is compiled into.
- Every code fix supports **Fix All** (document, project, solution), so the whole rewrite runs as one
  command:

  ```bash
  dotnet format analyzers <solution> --diagnostics PHLM001 PHLM002 … --severity info
  ```

### Rules

Severity `info` by default (the analyzer is a migration tool, not a gate); an application can raise
them in `.editorconfig` to stop new Fabrica.Watch calls appearing mid-migration.

| Rule | Detects (Fabrica.Watch) | Code fix (Pondhawk.Logging / MEL) |
|---|---|---|
| PHLM001 | `Trace/Debug/Info/Warning/Error(object? message)` and `(Exception, object? message)` | `LogTrace/LogDebug/LogInformation/LogWarning/LogError`; a non-string message gets `.ToString()` / a `{Value}` template |
| PHLM002 | `X(string template, params object[] args)` with positional holes (`{0}`, `{1:yyyy-MM-dd}`) | The matching `LogX` with **named** holes derived from each argument expression, format specifiers and alignment kept, repeated indices mapped to one name. Naming: the last two name segments, PascalCased (`kind` → `{Kind}`, `this.order.Id` → `{OrderId}`, `customer.Address.City` → `{AddressCity}`); when two *different* arguments collide, take one more leading segment (`{CustomerAddressCity}`), then a numeric suffix (`{OrderId2}`); no usable name (literal, call, arithmetic) → `{Arg0}`, `{Arg1}`, …. Names must be unique — two holes sharing a name keep only one value in the structured properties, silently |
| PHLM003 | Interpolated (`$"…"`) or concatenated message | Template + arguments (removes the per-call formatting cost and CA2254) |
| PHLM004 | `X(Func<string> expression)` | `if (logger.IsEnabled(LogLevel.X)) logger.LogX(…)` with the lambda body converted to a template where possible |
| PHLM005 | `IsTraceEnabled` / `IsDebugEnabled` / `IsInfoEnabled` / `IsWarningEnabled` / `IsErrorEnabled` | `IsEnabled(LogLevel.X)` |
| PHLM006 | `LeaveMethod()` in a `finally`, and logger acquisitions not in a `using` (`var logger = this.EnterMethod();`) | `using var logger = …;` and the `try/finally` removed when it only closed the logger |
| PHLM007 | `EnterScope(name)` / `LeaveScope(name)` pairs | A disposable `logger.EnterMethod(name)` scope around the bracketed statements |
| PHLM008 | `ErrorWithContext(ex, context)` with no message; `WarningWithContext(…)` | Adds a message (required by `ErrorWithContext`); `WarningWithContext` → `LogObject(LogLevel.Warning, "Context", context)` + `LogWarning(ex, message)` |
| PHLM009 | `Inspect(object name, value)` with a non-string name | `Inspect(string, object?)` — `nameof(...)` or `.ToString()` |
| PHLM010 | `LogObject(title, value)` | No rewrite needed; **report only**: notes the level moves Debug → Trace. `LogObject` without a level always logs at Trace — the payload is expensive to build — so no Debug alternative is offered |
| PHLM011 | `LogJson/LogXml(title, text, bool pretty)` | Drops `pretty` (Pondhawk logs content as given) |
| PHLM012 | `WatchFactoryLocator.Factory.GetLogger(category)` / `.GetLogger<T>()` / `.GetLogger(Type)` | `LoggingFactoryLocator.GetFactory().CreateLogger(…)` |
| PHLM013 | `this.EnterMethod<T>()` / `this.GetLogger()` / `EnterMethodSlim(category)` from `WatchExtensions` | `ObjectLoggingExtensions` equivalents (`object.EnterMethod()`, `object.GetLogger()`); `EnterMethodSlim` → `factory.CreateLogger(category).EnterMethod()` |
| PHLM014 | `Fabrica.Watch.SensitiveAttribute`; `Fabrica.Watch.Sink.PayloadType` | `Pondhawk.Logging.SensitiveAttribute`; `Pondhawk.Logging.PayloadType` |
| PHLM015 | `using Fabrica.Watch;` (and `.Sink`, `.Utilities`) once a file has no other Fabrica.Watch use | `using Microsoft.Extensions.Logging;` + `using Pondhawk.Logging;` |
| PHLM016 | `Fabrica.Watch.ILogger` as the type of a field, property, parameter, local or return value | `Microsoft.Extensions.Logging.ILogger` — with PHLM012/PHLM013 rewriting acquisition, the rewritten calls bind to MEL and most files compile straight after the Fix All; the type references it removes also let PHLM015 fire |
| PHLM020 | `CreateEvent(…)` + `LogEvent(…)` | **Report only.** Explains the levelled payload overloads (`LogJson(level, …)`, `LogText(level, …)`, `LogObject(level, …)`) and `ErrorWithContext`; hand-migrate |
| PHLM021 | `WatchFactoryBuilder` / `UseMongo` / `UseRealtime` / `UseQuiet` / `UseLocalSwitchSource` / `WatchFactoryLocator.SetFactory` | **Report only.** Points to `AddWatch` / `AddCloudWatch` / `AddJournaldConsole` and `LoggingFactoryLocator.SetFactory` (set once) |
| PHLM022 | Fabrica.Watch.Bridges `LoggerProvider` registered into MEL | **Report only.** The bridge is no longer needed — MEL is the API |
| PHLM023 | `GetCurrentScope` / `SetCurrentScope`, `Category` property reads | **Report only.** No equivalent; explain alternatives |

PHLM001–PHLM016 rewrite; PHLM020–PHLM023 report and link to the guide.

### Order of operations in an application

PHLM016 retypes the loggers and PHLM012/PHLM013 rewrite the common acquisitions, so after the Fix All
most files compile against Microsoft.Extensions.Logging. What does not is application-specific: the
bootstrap, base classes and helpers that hand out loggers, and Fabrica loggers supplied by dependency
injection. The supported sequence:

1. Add `Pondhawk.Logging` and `Pondhawk.Logging.Migration`; keep Fabrica.Watch compiling.
2. Run the Fix All for PHLM001–PHLM016 (the analyzer needs Fabrica.Watch's symbols to resolve).
3. Change the remaining logger acquisition (base classes, helpers, DI registrations, bootstrap) to
   Microsoft.Extensions.Logging, guided by PHLM020–PHLM023.
4. Build; fix what remains; remove Fabrica.Watch and the analyzer reference.

The application may not build between steps 2 and 3, so they land in the same pull request. The
commit from step 2 is still purely mechanical and can be reviewed by rule rather than by line.

### Testing

- `test/Pondhawk.Logging.Migration.Tests` with `Microsoft.CodeAnalysis.CSharp.Analyzer.Testing` /
  `.CodeFix.Testing`.
- A **Fabrica.Watch API stub** (source, in the test project) declaring the Fabrica.Watch types and
  members the rules match, with the same namespaces and names — so tests need no Fabrica binary.
- Per rule: detection cases, no-false-positive cases (an MEL `ILogger` with a same-named method must
  not trigger), the fix output, and Fix All across several documents.
- Template conversion (PHLM002) gets a table of tricky shapes: escaped braces, repeated indices,
  alignment + format (`{0,-10:N2}`), more args than holes, an exception argument first, string
  concatenation inside the template, and hole-name collisions (`a.Id` and `b.Id`; a collision that
  survives widening and takes a numeric suffix).
- A golden-file test: a realistic Fabrica.Watch class before and after the full Fix All with **all
  rules applied together** (PHLM006's `using`/`try`-removal overlaps the statements PHLM001/PHLM002
  rewrite, so the rules must compose, not only pass one at a time), compiled against Pondhawk.Logging
  to prove the output builds.

### Documentation

- `src/Pondhawk.Logging.Migration/README.md` — install, run, rule list.
- **`MIGRATING-FROM-FABRICA-WATCH.md`** at the repository root — the full guide:
  - The API map (below) and the behavioural differences (below).
  - Bootstrap: replacing `WatchFactoryBuilder` with `AddWatch`/`AddCloudWatch`/`AddJournaldConsole`;
    `ClearProviders()` and removing the Fabrica MEL bridge; `LoggingFactoryLocator.SetFactory` once,
    after the host is built.
  - Correlation: Fabrica stamped tenant/subject/correlation on each logger; Pondhawk providers read
    the ambient `IOperationContext` at `Post()`. How to populate it (`Pondhawk.Logging.AspNetCore`, or
    `Operation.Begin(...)` for background work).
  - Tests: one factory per test assembly (set-once locator); capture through an `ILoggerProvider`.
  - Switches: moving from the Fabrica Mongo switch collection to Watch Server switches, which use
    MEL's level names (`Quiet` → `None`, §5); method flow now visible at Debug (after §1), objects at
    Trace.
  - Retention: Fabrica's Mongo sink kept Trace/Debug for hours and Info+ for days; that is now the
    Watch Server's policy.
  - `[Sensitive]` masking now runs through System.Text.Json, not Newtonsoft: types that relied on
    Newtonsoft attributes (`[JsonIgnore]`, `[JsonProperty]`) to hide members must be checked.
  - Messages are now MEL templates, not `string.Format` — a message containing braces no longer throws.
  - Provider filters: the Watch switch filter is global; give other providers (e.g. OpenTelemetry)
    their own provider-scoped floor.
- Link the guide from `README.md`, `CLAUDE.md` and `llms.txt`.

**API map** (for the guide):

| Fabrica.Watch | Pondhawk.Logging / MEL |
|---|---|
| `Fabrica.Watch.ILogger` | `Microsoft.Extensions.Logging.ILogger` |
| `Level { Trace, Debug, Info, Warning, Error, Quiet }` | `LogLevel` (Info → Information, Quiet → None; MEL adds Critical) |
| `Debug(msg)`, `Info(msg)`, … | `LogDebug`, `LogInformation`, … |
| `X(template, args)` (`string.Format`) | `LogX(template, args)` (message template, named holes) |
| `X(Func<string>)` | `IsEnabled` guard + template |
| `IsDebugEnabled`, … | `IsEnabled(LogLevel.Debug)` |
| `EnterMethod()` (Debug) / `LeaveMethod()` / dispose | `using var logger = …EnterMethod();` (Debug after §1) |
| `EnterScope` / `LeaveScope` | disposable `EnterMethod(name)` scope |
| `Inspect(name, value)` → "Variable: name = (value)" | `Inspect(name, value)` → "name = value" (Debug) |
| `LogObject(title, obj)` (Debug) | `LogObject(title, obj)` (Trace) |
| `LogJson/LogXml/LogSql/LogYaml(title, text)` | same names (Debug), plus `LogLevel` overloads |
| `ErrorWithContext(ex, ctx, msg?)` | `ErrorWithContext(ex, ctx, msg)` (message required) |
| `WarningWithContext` | `LogObject(LogLevel.Warning, …)` + `LogWarning(ex, …)` |
| `CreateEvent` + `LogEvent` | levelled payload overloads |
| `this.GetLogger()` / `this.EnterMethod<T>()` | `object.GetLogger()` / `object.EnterMethod()` |
| `WatchFactoryLocator` (last set wins) | `LoggingFactoryLocator` (set once; `ResetForTesting()`) |
| `WatchFactoryBuilder.UseMongo/UseRealtime/UseQuiet` | `AddWatch` / `AddCloudWatch` / `AddJournaldConsole` / no provider |
| Mongo switch source | Watch Server switches (`WatchSwitchSource`) |
| Per-logger Tenant/Subject/CorrelationId | ambient `IOperationContext` |
| `Fabrica.Watch.SensitiveAttribute` | `Pondhawk.Logging.SensitiveAttribute` |
| Fabrica.Watch.Bridges (MEL → Watch) | not needed |

### Build and release

- Add the project to `pondhawk-logging.slnx` and to the Cake `Pack` target; `version.json` `1.0`.
- Published like the other packages (GitHub Packages on build, nuget.org by `publish.yml`).

---

## 3. Diagnostics readiness (`Pondhawk.Logging.AspNetCore`)

Goal: an application can delete Fabrica's request diagnostics middleware (`UseDiagnosticsMonitor`,
`UseRequestLogging` and the correlation stamping done through Fabrica's `ICorrelation`) and use this
package instead, without losing anything it relied on.

**Check, and fix here what fails:**

1. **Correlation id is the request's id everywhere.** The id `UseDiagnosticsMonitor` settles
   (header → ambient → new) is readable by application code through `IOperationContext` before any
   application middleware runs, so an application can put that same id in its error responses and
   audit records. Document the pattern in the package README.
2. **Exception middleware placed before it** still sees the settled id when it writes an error body
   (ordering guidance in the README).
3. **Subject and tenant** — the defaults (`preferred_username` → `client_id` → `sub` for the name;
   `sub`/name identifier/`client_id` for the id) cover token-based APIs; the `Subject` and `Tenant`
   delegates cover the rest. Add an example for a tenant taken from a claim.
4. **Request logging parity** with Fabrica's: request and response summary at Debug, bodies only when
   the category is open, `Authorization`/`Cookie`/`Set-Cookie` masked, body size cap. Confirm a body
   is never logged for a request skipped by `Skip`.
5. **Name collision during migration.** Fabrica.Api.Support defines `UseRequestLogging` and
   `UseDiagnosticsMonitor` on `IApplicationBuilder` too; a file importing both namespaces gets
   CS0121. Not a reason to rename — the Fabrica middleware is being removed — but the migration guide
   says to delete the Fabrica registrations in the same change, or call this package's methods through
   `DiagnosticsApplicationBuilderExtensions` explicitly while both exist.
6. **Per-request elevation — the diagnostic probe.** Fabrica let any caller raise a request's logging by
   sending `X-Diagnostics-Probe: 1`. It returns here as a **signed probe token** issued by Watch Server —
   designed below.

Any code change here is a minor version bump of `Pondhawk.Logging.AspNetCore`.

### Diagnostic probe

**What it is for.** Support, in production, turns logging up for one user's requests without touching
the switch table. Support issues a token in Watch Server; the user's client sends it on every request
until it expires, or support rotates the domain's key in Watch. Fabrica did this with `X-Diagnostics-Probe: 1`
(`Fabrica.Api.Support/Middleware/DiagnosticsMonitorMiddleware.cs`): any caller could send it, so any
caller could make a server log more — and, with the request dump, log their requests' bodies. Here the
header carries a token only Watch Server can make. The end user needs no role.

**The token.** Issued by Watch Server for one domain
(`watch-server/docs/plans/2026-10-15-diagnostic-probe-tokens.md`): a payload — version, token id, domain,
optional subject name, expiry — and an **ECDSA P-256** signature over it, each base64url, joined by a dot. Watch
holds the private key, one key pair per domain; the application gets the **public key** with its
switches, so it holds no secret and the domain needs no API key. A token **bound to a user** lasts up to
4 hours; an **unbound** one (for a user who cannot sign in, whose subject is `anonymous`) up to 1 hour.
There is no revocation list: tokens are short, and rotating the domain's key pair in Watch invalidates
every token it signed. The token id (`i`) is only for matching a refusal here to the issue in Watch's
audit trail; it is not checked against anything.

Watch keeps the private key in its configuration database in the clear (decided 2026-10-15: whoever could
read it could only raise logging that lands in Watch). Watch's limits apply only when it issues, so the
application **caps the expiry itself**: a token whose expiry is more than 4 hours away is refused,
whatever its signature, and a forged token stops within 4 hours.

**Where each part lives.**
- **`Pondhawk.Logging`** — the probe flag: `DiagnosticProbe`, set on and read from the request's
  `Activity` as a custom property (never baggage, so it is never sent on an outbound call), and an
  `IDiagnosticProbeKey` interface — the current domain name and public key, or none.
- **`Pondhawk.Logging.Watch`** — `AddWatch` registers an `IDiagnosticProbeKey` backed by the switch
  poll: `SwitchesResponse` gains the domain's probe public key, kept with the switch table and replaced
  on a rebind. The **switch filter** honours the flag: a probed request passes at **Trace** whatever the
  switch says (`level >= (probed ? Trace : switch level)`), and its events carry the tag "Diagnostics".
  CloudWatch and the journald console keep their own floors.
- **`Pondhawk.Logging.AspNetCore`** — validation, in the middlewares that already run per request:
  - **Monitor**: reads `options.ProbeHeader` (default `X-Diagnostics-Probe`, the same header Fabrica
    clients send). Rejects a value over 1 KB unparsed; checks the signature against the key from
    `IDiagnosticProbeKey`, the token's domain against the key's domain, and the expiry — not past, and
    not more than 4 hours away (a minute's allowance for clock skew either way). An **unbound** token sets the flag here, so the `Begin` line is probed.
  - **Enrichment**: a **bound** token is checked against `IOperationContext.SubjectName`, without regard
    to case, and sets the flag from here on — the endpoint, everything it calls, and the `End` line.
  - Off when no `IDiagnosticProbeKey` is registered (no Watch provider) or `options.Probe` is false.
- **The request dump follows by itself.** `UseRequestLogging` runs when `Pondhawk.Diagnostics.Http` is
  Debug-enabled; under a probe the switch filter enables every category at Trace, so a probed request is
  dumped — headers and bodies, with the usual masking — even when that category's switch is off.

**A refused token** — malformed, a payload version other than 1, no probe key from Watch yet (a token
used at once on a domain's first issue, before the next switch poll brings the key), bad signature, other
domain, expired, expiry beyond 4 hours, other user (Fabrica's plain `1` included) — is ignored, and the
request goes on **exactly as if no probe had been offered**: no exception, no change to the status,
headers or body, nothing for the endpoint to see. Checking a token never throws — a parse, base64, JSON
or cryptographic failure is caught and is one more refusal reason. The refusal is logged at Warning under
`Pondhawk.Diagnostics.Http` **once per distinct token per process** (a bounded cache of recent refusals),
at Debug after that, so a client still sending an expired token does not log a Warning per request. The
Warning names the token id when the token parsed, so support can find the issue in Watch's audit trail.

**Tests**: a valid unbound token probes from the monitor (the `Begin` line at Trace); a valid bound token
probes from enrichment for its user and not for another; expired, too-distant expiry (over 4 hours), tampered,
other-domain, oversized, non-base64, non-JSON and Fabrica-style `1` values are refused, with one Warning
each however many requests carry them, and each such request gets the same response as one with no probe
header; the flag
never reaches an outbound request; a probed request is dumped with the dump switch off; the filter passes
Trace for a probed request only; no probe without an `IDiagnosticProbeKey`; a rebind replaces the key; a
token refused for want of a key is accepted once a poll delivers it; a failed poll keeps the last key.

**Delivery to the user (decided 2026-10-15): copy and paste.** The token is about 200–230 characters
(the signature alone is 86). Support copies it from Watch and sends it by chat or email, or enters it
during a remote session; the client needs a support setting to paste it into, and sends it on every
request until it is cleared or expires.

**Migration guide**: Fabrica clients keep the header name; support issues a token in Watch instead of
setting `1`, and the client sends the token.

---

## 4. Documentation fixes

| Where | Says | Fix |
|---|---|---|
| `src/Pondhawk.Logging/README.md:85` (tests section) | `b.AddWatch("http://localhost:11000", "Tests")` | That two-argument overload does not exist; use `AddWatch("http://localhost:11000/Tests")`. |
| `src/Pondhawk.Logging/README.md:10`, `src/Pondhawk.Logging/CLAUDE.md:13` | "Fully standalone — no dependency on other Pondhawk packages" | `Pondhawk.Logging.csproj` references `Pondhawk.Context`, but no code in the package uses it — the providers get it transitively through this reference. Either **move** the `Pondhawk.Context` reference to the packages that use it (`.Watch`, `.CloudWatch`, `.AspNetCore`) and keep the claim true (preferred: the API package then depends only on MEL abstractions), or correct the claim. |
| `llms.txt:91` | `Subject` (`sub` claim, then name identifier) | Correct for the option's default, but misleading alone: the subject *name* on each event is `preferred_username` → `client_id` first. Match the AspNetCore README table (row at line 201). |
| everywhere method tracing is described | "Trace" | Debug, with §1. |

If the `Pondhawk.Context` reference moves, release the affected provider packages together with the
API so no published combination leaves a provider without `Pondhawk.Context`.

---

## 5. Switch level `None` (`Pondhawk.Logging.Watch`)

Watch Server is standardising on Microsoft.Extensions.Logging's level names and adding `None` (6) as a
switch level — see `watch-server/docs/plans/2026-10-15-mel-level-names.md`. Pondhawk.Logging already uses
`LogLevel` throughout (switches, the API, `LogEvent.Level` on the wire), so the names need nothing here;
only `None` does.

**Change.** `WatchSwitchSource` maps a switch level of 6 to `LogLevel.None`; today it clamps anything above
`Critical` to `Critical` (`src/Pondhawk.Logging.Watch/WatchSwitchSource.cs:163`). A value above 6 still
clamps, to `None`. With the switch filter at `None`, `IsEnabled` is false for every level of a matching
category, so the API skips all work for it.

**Why here.** Fabrica.Watch's `Quiet` maps to `None` (API map, §2), so migrated switch setups need it, and
the MCP `set_switch` tool can then silence a category instead of holding it at Critical.

**Order.** Independent of the server: a client without this change clamps a `None` switch to Critical
(nearly off), and this change does nothing until the server sends a 6.

**Tests**: a switch of 6 gives `LogLevel.None` and `IsEnabled` false for Critical; a value above 6 gives
`None`; 0–5 unchanged.

**Release.** `Pondhawk.Logging.Watch` minor: `3.2` → `3.3`.

---

## Order and releases

1. §4 documentation fixes and the `Pondhawk.Context` reference move — small, independent. §5 (switch
   level `None`, `Pondhawk.Logging.Watch` 3.3) is equally independent and can go with it.
2. §1 method tracing at Debug — `Pondhawk.Logging` 3.1, with release notes.
3. §3 diagnostics readiness — README guidance, plus any fixes (`Pondhawk.Logging.AspNetCore` minor).
4. §2 migration analyzer — `Pondhawk.Logging.Migration` 1.0 and `MIGRATING-FROM-FABRICA-WATCH.md`;
   validated by running it over a real Fabrica.Watch codebase before 1.0 is published to nuget.org.

## Decisions

- §1 ships as `Pondhawk.Logging` **3.1** — a behaviour change, no API change.
- Analyzer rules are **`info`** by default; an application raises them in `.editorconfig` to stop new
  Fabrica.Watch calls mid-migration.
- `LogObject` without a level always logs at **Trace**; PHLM010 reports the move from Debug and offers
  no alternative. The `LogObject(level, …)` overloads stay, for attaching an object to a failure event.
