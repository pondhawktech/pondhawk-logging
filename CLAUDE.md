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
dotnet build src/Pondhawk.Logging.AspNetCore/Pondhawk.Logging.AspNetCore.csproj

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

Five packages: the logging API, a Watch Server provider, a journald-optimized console, a CloudWatch Logs provider, and ASP.NET Core request diagnostics — the Watch, CloudWatch and AspNetCore packages build on the API. None depends on a Pondhawk package beyond that API. `Pondhawk.Logging.AspNetCore` is the one package that needs the ASP.NET Core shared framework.

### Pondhawk.Logging — Structured Logging API

The structured logging API on `Microsoft.Extensions.Logging`. No sink, no transport — provider packages (e.g. `Pondhawk.Logging.Watch`) build on it. Application code logs through the standard `ILogger`, so an app can drop this package and fall back to plain MEL with a configuration change and no code edits.

- **Logging API** (`Pondhawk.Logging` namespace): `LoggingExtensions` provides extensions on `ILogger`:
  - **`ILogger.EnterMethod()`** — disposable method-tracing scope with automatic entry/exit logging and elapsed time
  - **`ILogger.Inspect(name, value)`** — logs a name/value pair as `"{Name} = {Value}"` at Debug level
  - **`ILogger.LogObject(value)`** — serializes an object to a JSON payload (Trace)
  - **`ILogger.LogJson/LogSql/LogXml/LogYaml/LogText(title, content)`** — typed payload logging with syntax-highlighting hints (Debug)
  - **A `LogLevel` overload of every payload method** — `LogJson(level, title, json)`, `LogObject(level, title, value)`, … so a payload can ride on the failure event that needs it
  - **`ILogger.ErrorWithContext(cause, context, message)`** — an error carrying the exception plus a serialized context object (`Pondhawk.ErrorContext`)
  - Also: `LogPropertyNames` (public `Pondhawk.*` log-state property-name contract shared with sinks), `LogState` (the state the API attaches), serializers (`JsonObjectSerializer`), `PayloadType` enum, `[Sensitive]` attribute, `CorrelationManager` (correlation id as Activity baggage; subject and tenant as in-process Activity custom properties, never baggage), `TypeExtensions` (concise type names).
- **`LoggingFactoryLocator`**: process-wide `ILoggerFactory` behind `object.GetLogger()`/`EnterMethod()`. `SetFactory` is set-once by design; `ResetForTesting()` is the public escape hatch for test harnesses. Stand logging up once per *assembly* (NUnit `[SetUpFixture]`, xUnit assembly fixture), not per fixture — see the package README.
- **Logger acquisition**: the standard `ILoggerFactory` (`CreateLogger<T>()` / `CreateLogger(Type)` / `CreateLogger(string)`), returning `Microsoft.Extensions.Logging.ILogger`. Because the whole API gates on `ILogger.IsEnabled`, a provider that makes `IsEnabled` switch-aware makes the whole API skip work for switch-dropped categories.

### Pondhawk.Logging.Watch — Watch Server provider (references Pondhawk.Logging)

A ZLogger-based `Microsoft.Extensions.Logging` provider with Channel-based batching, dynamic switch-based level control, and MemoryPack delivery to a Watch Server.

- **WatchLoggerProcessor**: a ZLogger `IAsyncLogProcessor` with unbounded Channel batching. `Post()` runs on the calling thread — capturing the correlation id, subject and tenant from `Activity.Current` (`CorrelationManager`) and converting the pooled ZLogger entry to a Watch `LogEvent` (applying the matching switch's color and tag) — then queues it. A background task batches and posts. Circuit breaker for HTTP resilience.
- **WatchDestination**: the Watch URL (server, domain, optional API key) being delivered to, held as one immutable snapshot (URIs + version) that the processor re-reads per batch and the switch source per poll. **`Rebind(watchUrl)`** moves the process's events to a different Watch server at runtime without rebuilding the logging factory: nothing is torn down, so queued and critical-buffered events are still delivered, the batch domain and post URL always agree, the switch source drops its ETag and re-polls the new domain, and the circuit breaker resets. `AddWatch` registers it as a singleton for DI resolution. **`WatchDestination.Unbound()`** starts with no server named, for a host told where to log after startup: nothing is posted or polled, no failure is counted and the circuit stays shut, while Warning and above is held for the rebind that names a destination.
- **WatchLoggingBuilderExtensions**: **`AddWatch(this ILoggingBuilder, watchUrl, configure?)`** is the entry point — it opens the level floor and registers a `Microsoft.Extensions.Logging` filter driven by the switch table, then registers the ZLogger provider with the Watch processor and starts switch polling.
- **Switch-based level gating**: the filter matches a logger's category against the live switch table (`SwitchSource.Lookup`, longest prefix wins) and gates by the switch level. It is evaluated at `IsEnabled` — before the call site formats anything — so the whole logging API skips serialization for switch-dropped categories, with callers holding a plain `ILogger`. The short-circuit is per *category*: `IsEnabled` is true when any provider would keep the event, so a provider-scoped floor (the journald console's Warning) never short-circuits a call site on its own.
- **Switching**: Dynamic log level control via `SwitchSource`/`SwitchDef` with pattern matching. `WatchSwitchSource` polls a Watch Server for switch configuration.
- **LogEvent/LogEventBatch**: Event model serialized as MemoryPack for the wire; System.Text.Json (source-generated via `LogEventBatchContext`) available for debugging/testing.

### Pondhawk.Logging.Console — journald-optimized console

A ZLogger-based console for Linux production services. `AddJournaldConsole(this ILoggingBuilder)` wires a ZLogger console whose plain-text formatter prefixes each line with the sd-daemon priority (`<N>`, mapped from `LogLevel`) and the category, with no timestamp and no ANSI color — so journald parses the priority and stamps the time itself. Exceptions render inline (a single journald entry). Fixed at Warning via a provider-scoped filter. Depends only on ZLogger; does not reference `Pondhawk.Logging`.

### Pondhawk.Logging.CloudWatch — CloudWatch Logs provider (references Pondhawk.Logging)

A ZLogger-based provider that sends each event to Amazon CloudWatch Logs as one Pascal-case JSON object.

- **CloudWatchLoggingBuilderExtensions**: **`AddCloudWatch(this ILoggingBuilder, logGroup, service, configure?)`** is the entry point (an overload takes a `CloudWatchDestination`). It registers `CloudWatchLoggerProvider` and a filter scoped to it, fixed at Information: Information, Warning, Error and Critical always go, Debug and Trace never do, with no setting and no switch. Nothing here touches the network.
- **CloudWatchLoggerProvider**: a subclass of ZLogger's `ZLoggerLogProcessorLoggerProvider` that exists only so the level floor has its own provider type — every ZLogger processor otherwise shares one, and a floor scoped to that would clamp Watch too. A provider-scoped rule also outranks Watch's global switch filter, so the switch table does not gate CloudWatch.
- **CloudWatchLoggerProcessor**: a ZLogger `IAsyncLogProcessor` with unbounded Channel batching. `Post()` copies the pooled entry out on the calling thread (correlation, subject and tenant from `CorrelationManager`); formatting and sending happen on the flush thread. On the first batch it builds the AWS client, resolves the stream (`CloudWatchOptions.StreamName`, else `<instance-id>/<service>` from EC2 metadata) and creates the stream — and a missing group, with retention only if it created it. A verdict on the configuration turns it off, noted once on stdout: for the process (no client; no instance id after `MaxInstanceIdAttempts`) or for the group until a rebind (access denied on the stream or on a put). A request CloudWatch refuses as invalid is dropped, never re-held. Anything else pauses it for `PauseAfterFailure` (one minute; one 5-second attempt per call, no SDK retries). While paused or unbound the most recent `MaxHeldEvents` Warning-and-above events are held; they go out with the next batch, or on their own once the pause is over (the loop wakes for them), and get one last attempt on dispose. Everything that runs on the flush thread must neither throw nor loop: options are clamped at construction, `Post` refuses anything below Information itself, and only events worth holding are formatted during a pause. Batches are sorted by time and split at PutLogEvents' limits (1,048,576 bytes counting 26 per event; 10,000 events; a 24-hour span).
- **CloudWatchEventFormatter**: the event's JSON, shaped by what the event carries, never by guessing at text — an exception gives `Exceptions` (the whole chain, one frame per array entry) plus `Context` from `Pondhawk.ErrorContext`; a Json payload is nested JSON under `Payload`; anything else is text or an array of lines. `Context` is always an object (`{ "Value": … }` otherwise). Title, category, correlation id, exception messages and chain length are capped; an event still over 256 KB loses context, then payload lines, then stack frames, and failing that is written minimal with `Truncated: true`. `Format` never throws and its cut loop is bounded — it runs on the one flush thread.
- **CloudWatchDestination**: the log group, as one immutable snapshot re-read per batch. **`Rebind(logGroup)`** moves the process to another group without tearing anything down; **`Unbound()`** starts with no group named. `Sanitize` makes a valid group name from arbitrary text.

### Pondhawk.Logging.AspNetCore — ASP.NET Core request diagnostics (references Pondhawk.Logging)

Three middlewares that log through `ILogger` under the one category `Pondhawk.Diagnostics.Http` (`DiagnosticsLog`, `[LoggerMessage]` source-generated). No ZLogger and no dependency on a sink; a `FrameworkReference` to `Microsoft.AspNetCore.App`. Ported from `Pondhawk.Api`'s Serilog middlewares in `tools`, without its `IRequestContext` and gateway token.

- **DiagnosticsApplicationBuilderExtensions / DiagnosticsServiceCollectionExtensions**: `UseDiagnosticsMonitor`, `UseDiagnosticsEnrichment`, `UseRequestLogging`, and the optional **`AddPondhawkDiagnostics(configure?)`** that configures the one `DiagnosticsOptions` all three read (`IOptions<DiagnosticsOptions>`, so the defaults apply without it).
- **DiagnosticsMonitorMiddleware**: registered early — the outer bracket. "Begin {Method} {Path}" and "End {Method} {Path} -> {Status} ({Elapsed}ms)" at Debug, the End in a `finally`; path only, never the query string. A request that throws past it is reported as a 500. The End line carries the exception — the one that reached it, or the one an exception handler inside the bracket left on `IExceptionHandlerFeature`. `EndLevelByOutcome` (off by default) logs End at Information, Warning for 401/403/429 and Error for 5xx.
- **DiagnosticsEnrichmentMiddleware**: after authentication. Subject (`sub`, then name identifier, or a delegate) and tenant (a delegate) through `CorrelationManager`.
- **RequestCorrelation**: settles the correlation id once per request, by whichever of the monitor and enrichment runs first — so a request refused before authentication has one. Inbound header when well formed (1–64 ASCII letters, digits, `-`, `_`, `.`), else the ambient baggage under the same check, else a fresh Ulid. The echo is written in `Response.OnStarting`, because an exception handler clears headers set earlier.
- **RequestLoggingMiddleware / HttpDescriber**: registered deep. A no-op unless the category is Debug-enabled — nothing read, buffered or copied. Otherwise two events with JSON payloads (`LogJson`), JSON because Watch indents, colours and folds it. The request: `Method`, `Url` (raw, so encoding shows), `Query` (decoded; a repeated name is an array), `Endpoint` (name, route pattern, route values) and `User` (name, subject, scheme) — the reason it sits after routing and authentication — then `Headers` and `Body` (a JSON body nested as JSON, a form post as an object of fields, other text as a string). The response: `Status`, `Elapsed`, `Headers`, and `Body` only with `CaptureResponseBody`. What is said about a body goes beside it, never in it: `BodyOmitted` (with a `Reason`), `BodyCut`, `BodyError`. A request that throws gets no response event. The writer uses the relaxed JSON encoder so `&` and `+` stay readable.
- **Masking is by name and the application's choice**: only `Authorization`, `Cookie` and `Set-Cookie` are masked by default, keeping what gives nothing away (the scheme, cookie names, a `Set-Cookie`'s attributes). `MaskedHeaders`, `MaskedQueryParameters` and `MaskedBodyFields` name the rest, a masked value becoming its length (`***(7)`) — as sent for a header or query parameter, decoded for a form field, so a parsed and an unparsed form agree. `MaskedBodyFields` covers form fields and JSON properties at any depth, request and response; XML and plain text are not searched. **A JSON body that does not parse — malformed, or cut at the cap — is left out while any body field is named**, because the fields could not be found in it; with none named it is written as the string that was read.
- **Bodies**: a text request body (JSON, XML, `text/*`, form; not content-encoded) is read up to `MaxBodyBytes` (64 KB; one byte more to learn whether it was cut) and rewound; any other is described and not read. **A form already parsed** (`IFormFeature.Form` not null — OpenIddict's token endpoint, during authentication) is written from its fields rather than the stream, which may be spent; the cap then counts each field's name and value as UTF-8 and only whole fields are written, with `BodyCut` when one does not fit. **ResponseCaptureStream** replaces `Response.Body` for the one request, passes every write straight through and copies the first `MaxBodyBytes`; it decides whether to keep anything at the first write, when the content type is known. Before the real body is put back, bytes left unflushed in the response's pipe are flushed — that pipe was made over the capturing stream, and the server only completes the one in place afterwards — but only when there are some, since a flush starts the response, and never on the exception path, where the handler decides what the response is.
- `Skip` leaves a request out of the monitor and the dump, not out of correlation or enrichment.

Per-project deep-dives live in `src/Pondhawk.Logging/CLAUDE.md` and `src/Pondhawk.Logging.Watch/CLAUDE.md`.

## Conventions

- Namespaces match folder structure: `Pondhawk.Logging`, `Pondhawk.Logging.Watch`, `Pondhawk.Logging.Console`, `Pondhawk.Logging.CloudWatch`, `Pondhawk.Logging.AspNetCore`.

## History

Extracted from the [pondhawktech/tools](https://github.com/pondhawktech/tools) monorepo, where these lived as `src/Pondhawk.Logging` and `src/Pondhawk.Logging.Watch`. `Pondhawk.Logging` was consumed there by `Pondhawk.Api` (which stays in `tools` and now references it as a NuGet package). `Pondhawk.Logging.Watch` is consumed by the [pondhawk/watch-server](https://github.com/pondhawk/watch-server) client.

## CI/CD

- `.github/workflows/build.yml` — builds, tests, packs, and pushes the packages to the `pondhawktech` GitHub Packages feed on pushes to `main`; uploads `.nupkg` artifacts.
- `.github/workflows/publish.yml` — `workflow_dispatch` that promotes a build's artifacts to **NuGet.org** (requires the `NUGET_ORG_API_KEY` secret; org-level).
