# CLAUDE.md

This file provides guidance to Claude Code (claude.ai/code) when working with code in this repository.

## Build Commands

```bash
# Restore, build, and test via the Cake build script
dotnet run --project build/Build.csproj -- --target=Test

# Build the solution directly
dotnet build pondhawk-logging.slnx

# Build a single project
dotnet build src/Pondhawk.Logging/Pondhawk.Logging.csproj
dotnet build src/Pondhawk.Logging.Watch/Pondhawk.Logging.Watch.csproj
dotnet build src/Pondhawk.Logging.CloudWatch/Pondhawk.Logging.CloudWatch.csproj

# Run tests directly
dotnet test pondhawk-logging.slnx

# Run the CloudWatch provider against the real service (skipped unless a local AWS profile is named;
# creates and deletes throwaway log groups under /pondhawk-logging/test-*)
CLOUDWATCH_LIVE_PROFILE=<profile> dotnet test test/Pondhawk.Logging.CloudWatch.Tests --filter Category=Aws

# Pack NuGet packages (writes to ./artifacts)
dotnet run --project build/Build.csproj -- --target=Pack --build-number=<n>
```

## Project Setup

- **.NET 8** — every project targets `net8.0` (`LangVersion=latest`, `Nullable=enable`).
- **Central package management** via `Directory.Packages.props`.
- `TreatWarningsAsErrors` on; Meziantou analyzer enforced (`src/Directory.Build.props`).
- Versioning: each project's `version.json` holds `major.minor`; the Cake `Pack` target appends the build number (and a `-local`/`-<suffix>` prerelease tag off CI).

## Architecture

Four packages: the logging API, a Watch Server provider, a journald-optimized console, and a CloudWatch Logs provider — the Watch and CloudWatch providers build on the API. All are fully standalone — no dependency on other Pondhawk packages.

### Pondhawk.Logging — Structured Logging API

The structured logging API on `Microsoft.Extensions.Logging`. No sink, no transport — provider packages (e.g. `Pondhawk.Logging.Watch`) build on it. Application code logs through the standard `ILogger`, so an app can drop this package and fall back to plain MEL with a configuration change and no code edits.

- **Logging API** (`Pondhawk.Logging` namespace): `LoggingExtensions` provides extensions on `ILogger`:
  - **`ILogger.EnterMethod()`** — disposable method-tracing scope with automatic entry/exit logging and elapsed time
  - **`ILogger.Inspect(name, value)`** — logs a name/value pair as `"{Name} = {Value}"` at Debug level
  - **`ILogger.LogObject(value)`** — serializes an object to a JSON payload (Trace)
  - **`ILogger.LogJson/LogSql/LogXml/LogYaml/LogText(title, content)`** — typed payload logging with syntax-highlighting hints (Debug)
  - **A `LogLevel` overload of every payload method** — `LogJson(level, title, json)`, `LogObject(level, title, value)`, … so a payload can ride on the failure event that needs it
  - **`ILogger.ErrorWithContext(cause, context, message)`** — an error carrying the exception plus a serialized context object (`Pondhawk.ErrorContext`)
  - Also: `LogPropertyNames` (public `Pondhawk.*` log-state property-name contract shared with sinks), `LogState` (the state the API attaches), serializers (`JsonObjectSerializer`), `PayloadType` enum, `[Sensitive]` attribute, `CorrelationManager`, `TypeExtensions` (concise type names).
- **`LoggingFactoryLocator`**: process-wide `ILoggerFactory` behind `object.GetLogger()`/`EnterMethod()`. `SetFactory` is set-once by design; `ResetForTesting()` is the public escape hatch for test harnesses. Stand logging up once per *assembly* (NUnit `[SetUpFixture]`, xUnit assembly fixture), not per fixture — see the package README.
- **Logger acquisition**: the standard `ILoggerFactory` (`CreateLogger<T>()` / `CreateLogger(Type)` / `CreateLogger(string)`), returning `Microsoft.Extensions.Logging.ILogger`. Because the whole API gates on `ILogger.IsEnabled`, a provider that makes `IsEnabled` switch-aware makes the whole API skip work for switch-dropped categories.

### Pondhawk.Logging.Watch — Watch Server provider (references Pondhawk.Logging)

A ZLogger-based `Microsoft.Extensions.Logging` provider with Channel-based batching, dynamic switch-based level control, and MemoryPack delivery to a Watch Server.

- **WatchLoggerProcessor**: a ZLogger `IAsyncLogProcessor` with unbounded Channel batching. `Post()` runs on the calling thread — capturing the correlation id from `Activity.Current` and converting the pooled ZLogger entry to a Watch `LogEvent` (applying the matching switch's color and tag) — then queues it. A background task batches and posts. Circuit breaker for HTTP resilience.
- **WatchDestination**: the Watch URL (server, domain, optional API key) being delivered to, held as one immutable snapshot (URIs + version) that the processor re-reads per batch and the switch source per poll. **`Rebind(watchUrl)`** moves the process's events to a different Watch server at runtime without rebuilding the logging factory: nothing is torn down, so queued and critical-buffered events are still delivered, the batch domain and post URL always agree, the switch source drops its ETag and re-polls the new domain, and the circuit breaker resets. `AddWatch` registers it as a singleton for DI resolution. **`WatchDestination.Unbound()`** starts with no server named, for a host told where to log after startup: nothing is posted or polled, no failure is counted and the circuit stays shut, while Warning and above is held for the rebind that names a destination.
- **WatchLoggingBuilderExtensions**: **`AddWatch(this ILoggingBuilder, watchUrl, configure?)`** is the entry point — it opens the level floor and registers a `Microsoft.Extensions.Logging` filter driven by the switch table, then registers the ZLogger provider with the Watch processor and starts switch polling.
- **Switch-based level gating**: the filter matches a logger's category against the live switch table (`SwitchSource.Lookup`, longest prefix wins) and gates by the switch level. It is evaluated at `IsEnabled` — before the call site formats anything — so the whole logging API skips serialization for switch-dropped categories, with callers holding a plain `ILogger`. The short-circuit is per *category*: `IsEnabled` is true when any provider would keep the event, so a provider-scoped floor (the journald console's Warning) never short-circuits a call site on its own.
- **Switching**: Dynamic log level control via `SwitchSource`/`SwitchDef` with pattern matching. `WatchSwitchSource` polls a Watch Server for switch configuration.
- **LogEvent/LogEventBatch**: Event model serialized as MemoryPack for the wire; System.Text.Json (source-generated via `LogEventBatchContext`) available for debugging/testing.

### Pondhawk.Logging.Console — journald-optimized console

A ZLogger-based console for Linux production services. `AddJournaldConsole(this ILoggingBuilder)` wires a ZLogger console whose plain-text formatter prefixes each line with the sd-daemon priority (`<N>`, mapped from `LogLevel`) and the category, with no timestamp and no ANSI color — so journald parses the priority and stamps the time itself. Exceptions render inline (a single journald entry). Fixed at Warning via a provider-scoped filter. Depends only on ZLogger; does not reference `Pondhawk.Logging`.

### Pondhawk.Logging.CloudWatch — CloudWatch Logs provider (references Pondhawk.Logging)

A ZLogger-based provider that sends each event to Amazon CloudWatch Logs as one Pascal-case JSON object. Ported from fabrica-one's `Fabrica.Watch.CloudWatch` sink, which remains the reference for its behavior.

- **CloudWatchLoggingBuilderExtensions**: **`AddCloudWatch(this ILoggingBuilder, logGroup, service, configure?)`** is the entry point (an overload takes a `CloudWatchDestination`). It registers `CloudWatchLoggerProvider` and a filter scoped to it, fixed at Information: Information, Warning, Error and Critical always go, Debug and Trace never do, with no setting and no switch. Nothing here touches the network.
- **CloudWatchLoggerProvider**: a subclass of ZLogger's `ZLoggerLogProcessorLoggerProvider` that exists only so the level floor has its own provider type — every ZLogger processor otherwise shares one, and a floor scoped to that would clamp Watch too. A provider-scoped rule also outranks Watch's global switch filter, so the switch table does not gate CloudWatch.
- **CloudWatchLoggerProcessor**: a ZLogger `IAsyncLogProcessor` with unbounded Channel batching. `Post()` copies the pooled entry out on the calling thread (correlation from `CorrelationManager.Current`); formatting and sending happen on the flush thread. On the first batch it builds the AWS client, resolves the stream (`CloudWatchOptions.StreamName`, else `<instance-id>/<service>` from EC2 metadata) and creates the stream — and a missing group, with retention only if it created it. A verdict on the configuration turns it off, noted once on stdout: for the process (no client, no stream name) or for the group until a rebind (access denied). Anything else pauses it for `PauseAfterFailure` (one minute; one 5-second attempt per call, no SDK retries). While paused or unbound the most recent `MaxHeldEvents` Warning-and-above events are held for the next batch that can be sent. Batches are sorted by time and split at PutLogEvents' limits (1,048,576 bytes counting 26 per event; 10,000 events; a 24-hour span).
- **CloudWatchEventFormatter**: the event's JSON, shaped by what the event carries, never by guessing at text — an exception gives `Exceptions` (the whole chain, one frame per array entry) plus `Context` from `Pondhawk.ErrorContext`; a Json payload is nested JSON under `Payload`; anything else is text or an array of lines. `Context` is always an object (`{ "Value": … }` otherwise). An event over 256 KB loses context, then payload lines, then stack frames.
- **CloudWatchDestination**: the log group, as one immutable snapshot re-read per batch. **`Rebind(logGroup)`** moves the process to another group without tearing anything down; **`Unbound()`** starts with no group named. `Sanitize` makes a valid group name from arbitrary text.

Per-project deep-dives live in `src/Pondhawk.Logging/CLAUDE.md` and `src/Pondhawk.Logging.Watch/CLAUDE.md`.

## Conventions

- Namespaces match folder structure: `Pondhawk.Logging`, `Pondhawk.Logging.Watch`, `Pondhawk.Logging.Console`, `Pondhawk.Logging.CloudWatch`.

## History

Extracted from the [pondhawktech/tools](https://github.com/pondhawktech/tools) monorepo, where these lived as `src/Pondhawk.Logging` and `src/Pondhawk.Logging.Watch`. `Pondhawk.Logging` was consumed there by `Pondhawk.Api` (which stays in `tools` and now references it as a NuGet package). `Pondhawk.Logging.Watch` is consumed by the [pondhawk/watch-server](https://github.com/pondhawk/watch-server) client.

## CI/CD

- `.github/workflows/build.yml` — builds, tests, packs, and pushes the packages to the `pondhawktech` GitHub Packages feed on pushes to `main`; uploads `.nupkg` artifacts.
- `.github/workflows/publish.yml` — `workflow_dispatch` that promotes a build's artifacts to **NuGet.org** (requires the `NUGET_ORG_API_KEY` secret; org-level).
