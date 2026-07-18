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

# Run tests directly
dotnet test pondhawk-logging.slnx

# Pack NuGet packages (writes to ./artifacts)
dotnet run --project build/Build.csproj -- --target=Pack --build-number=<n>
```

## Project Setup

- **.NET 8** — both projects target `net8.0` (`LangVersion=latest`, `Nullable=enable`).
- **Central package management** via `Directory.Packages.props`.
- `TreatWarningsAsErrors` on; Meziantou analyzer enforced (`src/Directory.Build.props`).
- Versioning: each project's `version.json` holds `major.minor`; the Cake `Pack` target appends the build number (and a `-local`/`-<suffix>` prerelease tag off CI).

## Architecture

Two packages: the sink-agnostic logging API, and a Watch Server provider that builds on it. Both are fully standalone — no dependency on other Pondhawk packages.

### Pondhawk.Logging — Structured Logging API

The structured logging API on `Microsoft.Extensions.Logging`. No sink, no transport — provider packages (e.g. `Pondhawk.Logging.Watch`) build on it. Application code logs through the standard `ILogger`, so an app can drop this package and fall back to plain MEL with a configuration change and no code edits.

- **Logging API** (`Pondhawk.Logging` namespace): `LoggingExtensions` provides extensions on `ILogger`:
  - **`ILogger.EnterMethod()`** — disposable method-tracing scope with automatic entry/exit logging and elapsed time
  - **`ILogger.Inspect(name, value)`** — logs a name/value pair as `"{Name} = {Value}"` at Debug level
  - **`ILogger.LogObject(value)`** — serializes an object to a JSON payload
  - **`ILogger.LogJson/LogSql/LogXml/LogYaml/LogText(title, content)`** — typed payload logging with syntax-highlighting hints
  - Also: `LogPropertyNames` (public `Pondhawk.*` log-state property-name contract shared with sinks), `LogState` (the state the API attaches), serializers (`JsonObjectSerializer`), `PayloadType` enum, `[Sensitive]` attribute, `CorrelationManager`, `TypeExtensions` (concise type names).
- **Logger acquisition**: the standard `ILoggerFactory` (`CreateLogger<T>()` / `CreateLogger(Type)` / `CreateLogger(string)`), returning `Microsoft.Extensions.Logging.ILogger`. Because the whole API gates on `ILogger.IsEnabled`, a provider that makes `IsEnabled` switch-aware makes the whole API skip work for switch-dropped categories.

### Pondhawk.Logging.Watch — Watch Server provider (references Pondhawk.Logging)

A ZLogger-based `Microsoft.Extensions.Logging` provider with Channel-based batching, dynamic switch-based level control, and MemoryPack delivery to a Watch Server.

- **WatchLoggerProcessor**: a ZLogger `IAsyncLogProcessor` with unbounded Channel batching. `Post()` runs on the calling thread — capturing the correlation id from `Activity.Current` and converting the pooled ZLogger entry to a Watch `LogEvent` (applying the matching switch's color and tag) — then queues it. A background task batches and posts. Circuit breaker for HTTP resilience.
- **WatchLoggingBuilderExtensions**: **`AddWatch(this ILoggingBuilder, serverUrl, domain, configure?)`** is the entry point — it opens the level floor and registers a `Microsoft.Extensions.Logging` filter driven by the switch table, then registers the ZLogger provider with the Watch processor and starts switch polling.
- **Switch-based level gating**: the filter matches a logger's category against the live switch table (`SwitchSource.Lookup`, longest prefix wins) and gates by the switch level. It is evaluated at `IsEnabled` — before the call site formats anything — so the whole logging API skips serialization for switch-dropped categories, with callers holding a plain `ILogger`.
- **Switching**: Dynamic log level control via `SwitchSource`/`SwitchDef` with pattern matching. `WatchSwitchSource` polls a Watch Server for switch configuration.
- **LogEvent/LogEventBatch**: Event model serialized as MemoryPack for the wire; System.Text.Json (source-generated via `LogEventBatchContext`) available for debugging/testing.

Per-project deep-dives live in `src/Pondhawk.Logging/CLAUDE.md` and `src/Pondhawk.Logging.Watch/CLAUDE.md`.

## Conventions

- Namespaces match folder structure: `Pondhawk.Logging`, `Pondhawk.Logging.Watch`.

## History

Extracted from the [pondhawktech/tools](https://github.com/pondhawktech/tools) monorepo, where these lived as `src/Pondhawk.Logging` and `src/Pondhawk.Logging.Watch`. `Pondhawk.Logging` was consumed there by `Pondhawk.Api` (which stays in `tools` and now references it as a NuGet package). `Pondhawk.Logging.Watch` is consumed by the [pondhawk/watch-server](https://github.com/pondhawk/watch-server) client.

## CI/CD

- `.github/workflows/build.yml` — builds, tests, packs, and pushes both packages to the `pondhawktech` GitHub Packages feed on pushes to `main`; uploads `.nupkg` artifacts.
- `.github/workflows/publish.yml` — `workflow_dispatch` that promotes a build's artifacts to **NuGet.org** (requires the `NUGET_ORG_API_KEY` secret; org-level).
