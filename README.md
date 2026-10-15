<p align="center">
  <img src="pht-small-logo.png" alt="Pondhawk.Logging" width="120" />
</p>

<h1 align="center">Pondhawk.Logging</h1>

<p align="center">
  A Microsoft.Extensions.Logging structured logging API (method tracing, object/payload logging, [Sensitive] masking) plus a Watch Server provider — a ZLogger-based provider with batching and dynamic switch-based level control — a journald console, a CloudWatch Logs provider and ASP.NET Core request diagnostics.
</p>

<p align="center">
  <a href="https://github.com/pondhawktech/pondhawk-logging/actions/workflows/build.yml"><img src="https://github.com/pondhawktech/pondhawk-logging/actions/workflows/build.yml/badge.svg" alt="Build" /></a>
  <img src="https://img.shields.io/badge/.NET-8.0-512bd4" alt=".NET 8" />
  <img src="https://img.shields.io/badge/license-MIT-blue" alt="MIT License" />
  <a href="https://www.nuget.org/packages/Pondhawk.Logging"><img src="https://img.shields.io/nuget/v/Pondhawk.Logging?label=Logging" alt="Pondhawk.Logging on NuGet" /></a>
  <a href="https://www.nuget.org/packages/Pondhawk.Logging.Watch"><img src="https://img.shields.io/nuget/v/Pondhawk.Logging.Watch?label=Logging.Watch" alt="Pondhawk.Logging.Watch on NuGet" /></a>
  <a href="https://www.nuget.org/packages/Pondhawk.Logging.CloudWatch"><img src="https://img.shields.io/nuget/v/Pondhawk.Logging.CloudWatch?label=Logging.CloudWatch" alt="Pondhawk.Logging.CloudWatch on NuGet" /></a>
  <a href="https://www.nuget.org/packages/Pondhawk.Logging.AspNetCore"><img src="https://img.shields.io/nuget/v/Pondhawk.Logging.AspNetCore?label=Logging.AspNetCore" alt="Pondhawk.Logging.AspNetCore on NuGet" /></a>
</p>

Five packages, all `net8.0`, with no dependency on any Pondhawk package beyond the logging API itself and Pondhawk.Context, which holds the unit of work's correlation id, subject and tenant:

| Package | Description |
|---------|-------------|
| [**Pondhawk.Logging**](src/Pondhawk.Logging/README.md) | The structured logging API (method tracing, object/typed-payload logging, `[Sensitive]` masking) on `Microsoft.Extensions.Logging`. **No sink or transport** — providers build on it. |
| [**Pondhawk.Logging.Watch**](src/Pondhawk.Logging.Watch/README.md) | Watch Server provider for `Pondhawk.Logging` — a ZLogger-based provider with Channel-based batching and dynamic switch-based level control. |
| [**Pondhawk.Logging.Console**](src/Pondhawk.Logging.Console/README.md) | A ZLogger-based console optimized for systemd-journald (sd-daemon priority prefixes, no timestamp/color), fixed at Warning for Linux production services. |
| [**Pondhawk.Logging.CloudWatch**](src/Pondhawk.Logging.CloudWatch/README.md) | Amazon CloudWatch Logs provider for `Pondhawk.Logging` — each event one JSON object Logs Insights can filter on; creates its own stream and group; never blocks or stops the host. |
| [**Pondhawk.Logging.AspNetCore**](src/Pondhawk.Logging.AspNetCore/README.md) | ASP.NET Core request diagnostics for `Pondhawk.Logging` — correlation id, subject and tenant on every event of a request, a begin/end bracket with status and elapsed time, and an opt-in dump of the whole request. Needs the ASP.NET Core shared framework. |

Moving off **Fabrica.Watch**? [**Pondhawk.Logging.Migration**](src/Pondhawk.Logging.Migration/README.md) is a
development-only Roslyn analyzer (`netstandard2.0`) whose code fixes rewrite Fabrica.Watch calls to
`Microsoft.Extensions.Logging` and this API.

## Installation

```bash
dotnet add package Pondhawk.Logging
dotnet add package Pondhawk.Logging.Watch     # deliver events to a Watch Server
dotnet add package Pondhawk.Logging.Console   # journald-optimized console for Linux production
dotnet add package Pondhawk.Logging.CloudWatch # deliver events to Amazon CloudWatch Logs
dotnet add package Pondhawk.Logging.AspNetCore # request diagnostics for ASP.NET Core
```

## Pondhawk.Logging

Structured logging as extensions on `Microsoft.Extensions.Logging`'s `ILogger` (`using Pondhawk.Logging;`):

```csharp
using var _ = logger.EnterMethod();          // entry/exit + elapsed, disposable scope

logger.Inspect(nameof(orderId), orderId);    // "orderId = 4271" at Debug
logger.LogObject("order", order);            // serialize an object to a JSON payload (Trace)
logger.LogJson("payload", jsonString);       // typed payload with syntax-highlight hints (Debug)

// Every payload method takes an optional level, so the content explaining a failure
// rides on the event that reports it rather than on a Trace event production drops.
logger.LogJson(LogLevel.Error, "Malformed plan", planJson);
logger.LogObject(LogLevel.Error, "result", result);

// An error plus the state that surrounds it, on one event.
logger.ErrorWithContext(cause, new { InstanceId = id }, "Failed to deregister");
```

`[Sensitive]` masks a property when an object is serialized. Also included: the `PayloadType` enum, the `JsonObjectSerializer`, and the public `LogPropertyNames` contract that sinks read.

### Changing the Watch destination while running

`AddWatch(watchUrl)` fixes the destination for the life of the process. For an application told
where to log after it has started, hold a `WatchDestination` instead and rebind it — nothing is torn
down, so events already queued are delivered rather than dropped:

```csharp
var destination = new WatchDestination("http://localhost:11000/MyApp");
builder.Logging.AddWatch(destination);

destination.Rebind("http://watch.prod.internal:11000/MyApp.Fleet");
```

A host with no destination at startup starts `WatchDestination.Unbound()` instead of pointing at a
placeholder. While unbound nothing is posted, no failure is counted and the circuit stays shut; Warning
and above is held for the rebind that names a server.

### Acquiring loggers

Loggers come from the standard `ILoggerFactory` — there is no proprietary acquisition type:

```csharp
ILogger logger = loggerFactory.CreateLogger<OrderService>();   // or CreateLogger("My.Category")
```

The returned `ILogger` is the standard `Microsoft.Extensions.Logging.ILogger`, so code that logs through it is unchanged whether or not a provider (like Watch) is wired underneath. Because the API gates on `ILogger.IsEnabled`, a switch-aware provider makes the whole API skip work for switch-dropped categories.

## Pondhawk.Logging.Watch

Deliver events to a Watch Server and make the logging API switch-aware:

```csharp
using Microsoft.Extensions.Logging;
using Pondhawk.Logging.Watch;

// Recommended — the Watch Server controls levels via switches
builder.Logging.ClearProviders();
builder.Logging.AddWatch("http://localhost:11000/MyApp");
```

`AddWatch` registers a ZLogger delivery processor and a level filter driven by the Watch server's switch table, so payloads for switch-dropped categories are never serialized. Events are batched over an unbounded Channel and sent as MemoryPack, with a circuit breaker for HTTP resilience. See the [package README](src/Pondhawk.Logging.Watch/README.md) for switching and the event model.

## Pondhawk.Logging.Console

A console optimized for **systemd-journald** on Linux production servers — fixed at Warning:

```csharp
using Microsoft.Extensions.Logging;
using Pondhawk.Logging.Console;

builder.Logging.ClearProviders();
builder.Logging.AddJournaldConsole();
```

Each line carries an sd-daemon priority prefix (`<3>` err, `<4>` warning, …) so `journalctl -p` filtering and level coloring work, with no timestamp or ANSI color (journald adds the time and stores raw text) and exceptions rendered inline as a single entry. See the [package README](src/Pondhawk.Logging.Console/README.md).

## Pondhawk.Logging.CloudWatch

Information and above to **Amazon CloudWatch Logs**, one JSON object per event:

```csharp
using Microsoft.Extensions.Logging;
using Pondhawk.Logging.CloudWatch;

builder.Logging.AddCloudWatch("/my-app/Production", "orders");   // stream: <instance-id>/orders
```

Each event carries `Level`, `Category`, `CorrelationId` and `Title`, with an exception written as a structured `Exceptions` chain, an `ErrorWithContext` context as a nested `Context` object, and a JSON payload as nested JSON — so Logs Insights filters on fields (`filter Context.OrderId = 42`). The provider creates its stream, and a missing group with 30 days' retention, on its first batch; adding it touches no network, and a CloudWatch that is unreachable, denied or unconfigured never throws into or delays the host. A `CloudWatchDestination` can start unbound and be rebound when the group is learned later. See the [package README](src/Pondhawk.Logging.CloudWatch/README.md).

## Pondhawk.Logging.AspNetCore

Request diagnostics for **ASP.NET Core**, as three middlewares:

```csharp
using Pondhawk.Logging.AspNetCore;

builder.Services.AddPondhawkDiagnostics(options => options.MaskedBodyFields.Add("password"));

app.UseDiagnosticsMonitor();      // early: Begin/End with status and elapsed time, and the correlation id
app.UseAuthentication();
app.UseDiagnosticsEnrichment();   // after authentication: subject and tenant on every event
app.UseRequestLogging();          // deep: the whole request and its response, when the category is at Debug
```

Everything is logged under `Pondhawk.Diagnostics.Http`, which takes its own switch. An inbound `X-Correlation-Id` is validated before it is used and echoed back. A request that fails carries its exception on the monitor's End line. The request dump is a JSON payload — URL, decoded query, the endpoint and user it resolved to, headers and the body (a JSON body nested as JSON) up to 64 KB — that Watch indents, colours and folds, followed by the response the same way (status, elapsed time, headers, and the body when asked for). It shows what was sent, less `Authorization`, `Cookie` and `Set-Cookie` values; anything else is masked only when the application names it, so name password fields. See the [package README](src/Pondhawk.Logging.AspNetCore/README.md).

## Repository Layout

```
src/Pondhawk.Logging/          The logging API on Microsoft.Extensions.Logging (net8.0)
src/Pondhawk.Logging.Watch/    Watch Server provider — ZLogger processor + switching (net8.0)
src/Pondhawk.Logging.Console/  journald-optimized ZLogger console (net8.0)
src/Pondhawk.Logging.CloudWatch/  CloudWatch Logs provider — ZLogger processor (net8.0)
src/Pondhawk.Logging.AspNetCore/  ASP.NET Core request diagnostics — three middlewares (net8.0)
test/Pondhawk.Logging.Tests/
test/Pondhawk.Logging.Watch.Tests/
test/Pondhawk.Logging.Console.Tests/
test/Pondhawk.Logging.CloudWatch.Tests/
test/Pondhawk.Logging.AspNetCore.Tests/
build/                         Cake (Frosting) build script
.github/workflows/             build.yml (build/test/pack → GitHub Packages) + publish.yml (→ NuGet.org)
```

## Building

```bash
# Restore, build, and test via the Cake script
dotnet run --project build/Build.csproj -- --target=Test

# Or use the SDK directly
dotnet build pondhawk-logging.slnx
dotnet test pondhawk-logging.slnx
```

## Versioning & Packaging

Each package's version is `major.minor` from its `version.json` with a build number as the patch. Off-CI builds get a `-local` prerelease suffix.

```bash
dotnet run --project build/Build.csproj -- --target=Pack --build-number=<n>   # writes ./artifacts/*.nupkg
```

## CI/CD

- **`build.yml`** — on push/PR to `main`: build + test; on `main` it also packs and pushes the packages to the `pondhawktech` GitHub Packages feed and uploads the `.nupkg`s as an artifact.
- **`publish.yml`** — manual `workflow_dispatch` that promotes a build's artifacts to **NuGet.org** (uses the org-level `NUGET_ORG_API_KEY`).

## History

Extracted from the [pondhawktech/tools](https://github.com/pondhawktech/tools) monorepo. `Pondhawk.Logging` was consumed there by `Pondhawk.Api` (which stays in `tools` and now references it as a NuGet package). `Pondhawk.Logging.Watch` is consumed by the [pondhawk/watch-server](https://github.com/pondhawk/watch-server) client.

## License

MIT — see [LICENSE](LICENSE).
