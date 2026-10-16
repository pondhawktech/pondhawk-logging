# Plan: no package references Pondhawk.Logging

Status: **IMPLEMENTED** — 2026-10-16. Decided 2026-10-16: the probe key goes through a keyed DI service
(§3a), and `DiagnosticProbe` stays public in Pondhawk.Logging (§2). What differed from the plan is under
"As built" at the end.

## Why

The rule, in the user's words: "Pondhawk.Logging is meant to be vendored in to each app that uses it. We
want consumers of Pondhawk.Logging to be capable of dropping all the Pondhawk.Logging dependency without
breaking the consuming app."

Today that does not hold. `Pondhawk.Logging.Watch`, `.CloudWatch` and `.AspNetCore` each have a
`ProjectReference` to `src/Pondhawk.Logging`, so each package depends on the `Pondhawk.Logging` NuGet
package. `Pondhawk.Logging.Console` references only ZLogger.

- **Vendoring breaks the build.** An app that vendors Pondhawk.Logging's source and installs a sink also
  gets `Pondhawk.Logging.dll`. Every Pondhawk.Logging type then exists twice, and `LogJson(…)` or
  `PayloadType` fail to compile as ambiguous (CS0433/CS0121).
- **Dropping it is impossible.** An app cannot remove Pondhawk.Logging while the sinks bring it in.

The API and the sinks already talk only through names and values on the log state, never through
types. This plan makes that the only contract between them.

## What the packages use from Pondhawk.Logging

Checked 2026-10-16 on `c6342d2`, by searching each package for every public Pondhawk.Logging type (the
packages' namespaces sit under `Pondhawk.Logging`, so they need no `using` to see them):

| Type | Watch | CloudWatch | AspNetCore |
|---|---|---|---|
| `LogPropertyNames` (`Nesting`, `PayloadType`, `PayloadContent`, `ErrorContext`) | `WatchLoggerProcessor.cs:223-232` | `CloudWatchLoggerProcessor.cs:260-269` | — |
| `PayloadType` (`Text` = 4, `Json` = 1) | `WatchLoggerProcessor.cs:245, 253` | `CloudWatchEvent` (internal), `CloudWatchLoggerProcessor.cs:264`, `CloudWatchEventFormatter.cs:102` | — |
| `DiagnosticProbe` | `IsActive`: `WatchLoggingBuilderExtensions.cs:121`, `WatchLoggerProcessor.cs:208` | — | `Begin()`: `RequestProbe.cs:74` |
| `IDiagnosticProbeKey`, `DiagnosticProbeKey` | `WatchSwitchSource` (public) implements and builds it; `AddWatch` registers it (`WatchLoggingBuilderExtensions.cs:124`) | — | resolved in `RequestProbe.cs:37`; the public `ProbeToken.Verify` takes it |
| `LoggingExtensions.LogJson(level, …)` | — | — | `RequestLoggingMiddleware.cs:63, 87` |

Nothing else is used. Pondhawk.Logging itself never uses the three probe types; they went into it in
`e7af0cf` (2026-10-15) only because it was the one package both Watch and AspNetCore referenced.
`LogPropertyNames.CorrelationId` and `.Prefix` are used nowhere.

---

## 1. Property names and payload types: a copy in each package

Watch and CloudWatch each get an **internal** copy of the four property names they read, and of the
`PayloadType` numbers they use. CloudWatch's internal `CloudWatchEvent.PayloadType` becomes an internal
enum or an `int`.

**Guard against drift.** A test in each package's test project checks the copy against Pondhawk.Logging's
`LogPropertyNames` and `PayloadType`. Test projects may keep referencing Pondhawk.Logging, since tests
don't ship.

## 2. The diagnostic probe marker: a copy on each side

The contract is the `Activity` custom property `"Pondhawk.DiagnosticProbe"` (looked up the parent chain)
and the tag `"Diagnostics"`.
- AspNetCore keeps an internal copy of `Begin()`.
- Watch keeps an internal copy of `IsActive` and `Tag`.
- A test on each side checks the property name and tag against `Pondhawk.Logging.DiagnosticProbe`.

**Decided: `DiagnosticProbe` stays public in Pondhawk.Logging.** An app may call it itself, for example to
probe a background job. Keeping it costs nothing, because the sinks no longer reference it. Its XML docs
should say the property name and tag are a contract that the sinks' copies follow.

## 3. The probe key hand-off — decided: (a)

The key is the only real shared type. `WatchSwitchSource` gets the domain's public key with the switch
table and replaces it when the switches are re-polled or rebound, so the hand-off must stay a **live
lookup** (`IDiagnosticProbeKey.Current` today), never a value captured at startup.

**(a) Built-in .NET types only — chosen.**
- Watch registers a keyed singleton under a fixed key, `"Pondhawk.DiagnosticProbeKey"`. It is a function
  returning the current `(string Domain, ReadOnlyMemory<byte> PublicKey)?`.
- AspNetCore resolves it by that key.
- No shared type at all; the contract is the key string and the delegate's shape.
- `ProbeToken.Verify` takes the domain and key bytes directly.
- **Caveat:** keyed services need a container that supports them. Microsoft.Extensions.DependencyInjection
  8+ does, and so does Autofac.Extensions.DependencyInjection 9+ (verify while implementing). An app on a container without them gets no
  key, so probe tokens are refused (as with no Watch provider) and nothing else breaks.

**(b) Move `IDiagnosticProbeKey` and `DiagnosticProbeKey` into Pondhawk.Context — not chosen.**
- Watch and AspNetCore already reference Pondhawk.Context, so the types and signatures stay as they are.
- This changes another package (`~/repositories/pondhawk-context`).
- It assumes Pondhawk.Context is never vendored; if it were, it would have this same problem.

## 4. AspNetCore writes its payload itself

`RequestLoggingMiddleware`'s two `LogJson(LogLevel.Debug, title, json)` calls become an internal helper.
It builds the same state `LoggingExtensions` builds: a `KeyValuePair` list carrying
`Pondhawk.PayloadType` = 1 and `Pondhawk.PayloadContent` = the JSON, formatted as the title (the shape of
Pondhawk.Logging's internal `LogState`).

A test checks that the helper's event and `LogJson`'s event carry the same properties and message.

## 5. Remove the references

- Delete the `ProjectReference` to `Pondhawk.Logging` from Watch, CloudWatch and AspNetCore.
- Delete `IDiagnosticProbeKey` and `DiagnosticProbeKey` from Pondhawk.Logging. `DiagnosticProbe` stays
  (§2).

**Enforce the rule.** Each package's test project gets a test that the shipped assembly does not reference
`Pondhawk.Logging`, checked with `GetReferencedAssemblies()`. Add a check in the Cake build or CI that no
`src/*/*.csproj` other than `Pondhawk.Logging`'s own references it, so a future change cannot quietly put
it back.

## 6. Docs

- **Root `CLAUDE.md`:** today it says "the Watch, CloudWatch and AspNetCore packages build on the API".
  Reverse that and update each package's "(references Pondhawk.Logging)" heading.
- **The rule, written down:**
  - Pondhawk.Logging is meant to be vendored into each app that uses it.
  - No package in this repository references it.
  - The contract between the API and the sinks is property names and values on the log state, plus the
    probe's Activity property, and nothing else.
- **READMEs:**
  - each package's README
  - a vendoring section in Pondhawk.Logging's README: how to vendor it, what to keep in step, and that
    dropping it falls back to plain Microsoft.Extensions.Logging
  - `MIGRATING-FROM-FABRICA-WATCH.md`, where it assumes the sinks bring the API in
- **`src/Pondhawk.Logging/CLAUDE.md`, `src/Pondhawk.Logging.Watch/CLAUDE.md`:** the `LogPropertyNames`
  "shared with sinks" wording and the probe types.

## Versions

These are breaking releases. An app that calls `LogJson` but gets Pondhawk.Logging only through a sink
stops compiling, and public signatures change (`WatchSwitchSource`'s interface, `ProbeToken.Verify`).

| Package | From | To |
|---|---|---|
| `Pondhawk.Logging` (the two key types removed) | 3.3 | **4.0** |
| `Pondhawk.Logging.Watch` | 3.4 | **4.0** |
| `Pondhawk.Logging.CloudWatch` | 1.2 | **2.0** |
| `Pondhawk.Logging.AspNetCore` | 1.2 | **2.0** |

Console and Migration are unchanged.

## Follow-up for consumers

- **fabrica-one:** it references `Pondhawk.Logging` directly and calls its API, so the bump needs no code
  change. Check that once the packages are published.
- **watch-server** (owned by the watch-server session, once the packages are on NuGet.org):
  - **The Pump** (`src/Pondhawk.Watch.Pump`) references only `Pondhawk.Logging.Watch`, but calls
    `EnterMethod`, `Inspect`, `LogJson`, `LogObject`, `LogSql`, `LogText`, `LogXml` and `LogYaml`. It is
    the transitive case this plan breaks, so it needs its own `Pondhawk.Logging` 4.0 reference when it
    moves to Watch 4.0.
  - **The Server** already references `Pondhawk.Logging` and `Pondhawk.Logging.CloudWatch` directly. It
    needs only the bumps, to Logging 4.0 and CloudWatch 2.0.
  - **The explorer client** uses no Pondhawk.Logging package and is unaffected.

## As built (2026-10-16)

- **The copies** are `LogStateContract` (Watch, CloudWatch) and `ProbeContract` (Watch: `IsActive`, `Tag`,
  `KeyServiceKey`; AspNetCore: `Begin`, `KeySource`, `KeyServiceKey`). AspNetCore's payload helper is
  `JsonPayload.Log`. CloudWatch's internal `CloudWatchEvent.PayloadType` is now an `int`.
- **The key** is `WatchSwitchSource.CurrentProbeKey` (internal), registered by `AddWatch` as a keyed
  `Func<(string Domain, ReadOnlyMemory<byte> PublicKey)?>` under `"Pondhawk.DiagnosticProbeKey"`.
  `ProbeContract.KeySource` resolves it only through `IKeyedServiceProvider`, so a container without keyed
  services gets no key instead of an exception. The Autofac version that supports keyed services was not
  checked.
- **`ProbeToken` was already internal**, so its signature change (an internal `ProbeKey` record) is not public.
  `WatchSwitchSource` no longer implements `IDiagnosticProbeKey`; that is the public change in Watch.
- **Tests:** a `ContractTests` class in the Watch, CloudWatch, AspNetCore and Console test projects. Each checks
  that its package's assembly does not reference Pondhawk.Logging; Watch and CloudWatch check their copies
  against `LogPropertyNames` and `PayloadType`; Watch and AspNetCore check the probe mark against
  `DiagnosticProbe` both ways; AspNetCore checks `JsonPayload.Log` against `LogJson`, and finds the key
  `AddWatch` registers (its test project now references Watch for that).
- **Build:** the Cake `CheckReferences` task runs before `Build` and fails on a `ProjectReference` or
  `PackageReference` to Pondhawk.Logging from any other source project. Checked by adding one back: it fails.
- **Checked end to end**, with the packages packed locally:
  - none of Watch, CloudWatch, AspNetCore or Console lists Pondhawk.Logging as a dependency;
  - an ASP.NET Core app that vendors `src/Pondhawk.Logging` beside all four builds with no warnings, and its
    `LogJson`, `WarningWithContext` and `EnterMethod` calls reach the console;
  - the same app without Pondhawk.Logging builds and logs through plain Microsoft.Extensions.Logging;
  - the vendoring app against the published packages (Watch 3.4.66 and the rest) fails with CS0121
    ambiguity errors, which is the problem this plan fixes.
