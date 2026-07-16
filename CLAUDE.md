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

- **.NET 10** — both projects target `net10.0` (`LangVersion=latest`, `Nullable=enable`).
- **Central package management** via `Directory.Packages.props`.
- `TreatWarningsAsErrors` on; Meziantou analyzer enforced (`src/Directory.Build.props`).
- Versioning: each project's `version.json` holds `major.minor`; the Cake `Pack` target appends the build number (and a `-local`/`-<suffix>` prerelease tag off CI).

## Architecture

Two packages: the sink-agnostic logging API, and a Watch Server provider that builds on it. Both are fully standalone — no dependency on other Pondhawk packages.

### Pondhawk.Logging — Structured Logging API + `ILoggerSource`

The Serilog-based logging API and the logger-acquisition abstraction. No sink, no transport — provider packages (e.g. `Pondhawk.Logging.Watch`) build on it.

- **Logging API** (`Pondhawk.Logging` namespace): `SerilogExtensions` provides extensions on `Serilog.ILogger`:
  - **`ILogger.EnterMethod()`** — disposable method-tracing scope with automatic entry/exit logging and elapsed time
  - **`ILogger.Inspect(name, value)`** — logs a name/value pair as `"{Name} = {Value}"` at Debug level
  - **`ILogger.LogObject(value)`** — serializes an object to a JSON payload
  - **`ILogger.LogJson/LogSql/LogXml/LogYaml/LogText(title, content)`** — typed payload logging with syntax-highlighting hints
  - Also: `LogPropertyNames` (public, neutralized `Pondhawk.*` property-name contract shared with sinks), serializers (`JsonObjectSerializer`), `PayloadType` enum, `[Sensitive]` attribute, `CorrelationManager`, `TypeExtensions` (concise type names).
- **`ILoggerSource`**: the single seam an app injects to obtain category-scoped loggers — `CreateLogger<T>()` / `CreateLogger(Type)` / `CreateLogger(string)`, all returning `Serilog.ILogger`. `SerilogLoggerSource` is the canonical-Serilog default (`root.ForContext(SourceContext, category)`). A provider supplies a smarter one; an app can implement its own and drop the Watch package entirely with handlers unchanged. (Named `CreateLogger`, not `For`, to avoid analyzer rule CA1716.)

### Pondhawk.Logging.Watch — Watch Server provider (references Pondhawk.Logging)

A Serilog `ILogEventSink` with Channel-based batching, dynamic switch-based level control, and a switch-aware `ILoggerSource`.

- **WatchSink**: `ILogEventSink` with unbounded Channel batching. Converts Serilog events to Watch `LogEvent` instances with per-event switch-based filtering (`SwitchSource.Lookup`). Circuit breaker for HTTP resilience.
- **WatchSinkExtensions**: Serilog config extensions. **`UseWatch(serverUrl, domain)`** is the primary API — sets `MinimumLevel.Verbose()` and adds the sink so the Watch Server controls filtering via switches. `WriteTo.Watch()` is the lower-level alternative. Out-param overloads (`UseWatch(..., out SwitchSource)`, `Watch(..., out SwitchSource)`) expose the switch source so the root can share one instance with a `WatchLoggerSource`.
- **WatchLogger / WatchLoggerSource**: `WatchLogger` is an internal `ILogger` whose `IsEnabled` consults the live switch table for its category; because the logging API gates on `IsEnabled` (a real, virtually-dispatched interface member), the whole API becomes switch-aware — payloads are not serialized for switch-dropped categories — while callers hold a plain `ILogger`. `WatchLoggerSource` (public `ILoggerSource`) hands these out, sharing one `SwitchSource` with the sink.
- **Switching**: Dynamic log level control via `SwitchSource`/`SwitchDef` with pattern matching (longest prefix wins). `WatchSwitchSource` polls a Watch Server for switch configuration.
- **LogEvent/LogEventBatch**: Event model serialized as MemoryPack+Brotli for the wire; System.Text.Json (source-generated via `LogEventBatchContext`) available for debugging/testing.

Per-project deep-dives live in `src/Pondhawk.Logging/CLAUDE.md` and `src/Pondhawk.Logging.Watch/CLAUDE.md`.

## Conventions

- Namespaces match folder structure: `Pondhawk.Logging`, `Pondhawk.Logging.Watch`.

## History

Extracted from the [pondhawktech/tools](https://github.com/pondhawktech/tools) monorepo, where these lived as `src/Pondhawk.Logging` and `src/Pondhawk.Logging.Watch`. `Pondhawk.Logging` was consumed there by `Pondhawk.Api` (which stays in `tools` and now references it as a NuGet package). `Pondhawk.Logging.Watch` is consumed by the [pondhawk/watch-server](https://github.com/pondhawk/watch-server) client.

## CI/CD

- `.github/workflows/build.yml` — builds, tests, packs, and pushes both packages to the `pondhawktech` GitHub Packages feed on pushes to `main`; uploads `.nupkg` artifacts.
- `.github/workflows/publish.yml` — `workflow_dispatch` that promotes a build's artifacts to **NuGet.org** (requires the `NUGET_ORG_API_KEY` secret; org-level).
