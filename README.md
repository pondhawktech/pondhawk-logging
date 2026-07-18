<p align="center">
  <img src="pht-small-logo.png" alt="Pondhawk.Logging" width="120" />
</p>

<h1 align="center">Pondhawk.Logging</h1>

<p align="center">
  A Microsoft.Extensions.Logging structured logging API (method tracing, object/payload logging, [Sensitive] masking) plus a Watch Server provider — a ZLogger-based provider with batching and dynamic switch-based level control.
</p>

<p align="center">
  <a href="https://github.com/pondhawktech/pondhawk-logging/actions/workflows/build.yml"><img src="https://github.com/pondhawktech/pondhawk-logging/actions/workflows/build.yml/badge.svg" alt="Build" /></a>
  <img src="https://img.shields.io/badge/.NET-8.0-512bd4" alt=".NET 8" />
  <img src="https://img.shields.io/badge/license-MIT-blue" alt="MIT License" />
  <a href="https://www.nuget.org/packages/Pondhawk.Logging"><img src="https://img.shields.io/nuget/v/Pondhawk.Logging?label=Logging" alt="Pondhawk.Logging on NuGet" /></a>
  <a href="https://www.nuget.org/packages/Pondhawk.Logging.Watch"><img src="https://img.shields.io/nuget/v/Pondhawk.Logging.Watch?label=Logging.Watch" alt="Pondhawk.Logging.Watch on NuGet" /></a>
</p>

Three packages, all `net8.0` and fully standalone (no dependency on other Pondhawk packages):

| Package | Description |
|---------|-------------|
| [**Pondhawk.Logging**](src/Pondhawk.Logging/README.md) | The structured logging API (method tracing, object/typed-payload logging, `[Sensitive]` masking) on `Microsoft.Extensions.Logging`. **No sink or transport** — providers build on it. |
| [**Pondhawk.Logging.Watch**](src/Pondhawk.Logging.Watch/README.md) | Watch Server provider for `Pondhawk.Logging` — a ZLogger-based provider with Channel-based batching and dynamic switch-based level control. |
| [**Pondhawk.Logging.Console**](src/Pondhawk.Logging.Console/README.md) | A ZLogger-based console optimized for systemd-journald (sd-daemon priority prefixes, no timestamp/color), fixed at Warning for Linux production services. |

## Installation

```bash
dotnet add package Pondhawk.Logging
dotnet add package Pondhawk.Logging.Watch     # deliver events to a Watch Server
dotnet add package Pondhawk.Logging.Console   # journald-optimized console for Linux production
```

## Pondhawk.Logging

Structured logging as extensions on `Microsoft.Extensions.Logging`'s `ILogger` (`using Pondhawk.Logging;`):

```csharp
using var _ = logger.EnterMethod();          // entry/exit + elapsed, disposable scope

logger.Inspect(nameof(orderId), orderId);    // "orderId = 4271" at Debug
logger.LogObject("order", order);            // serialize an object to a JSON payload
logger.LogJson("payload", jsonString);       // typed payload with syntax-highlight hints
```

`[Sensitive]` masks a property when an object is serialized. Also included: `CorrelationManager`, the `PayloadType` enum, the `JsonObjectSerializer`, and the public `LogPropertyNames` contract that sinks read.

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
builder.Logging.AddWatch("http://localhost:11000", "MyApp");
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

## Repository Layout

```
src/Pondhawk.Logging/          The logging API on Microsoft.Extensions.Logging (net8.0)
src/Pondhawk.Logging.Watch/    Watch Server provider — ZLogger processor + switching (net8.0)
src/Pondhawk.Logging.Console/  journald-optimized ZLogger console (net8.0)
test/Pondhawk.Logging.Tests/
test/Pondhawk.Logging.Watch.Tests/
test/Pondhawk.Logging.Console.Tests/
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

- **`build.yml`** — on push/PR to `main`: build + test; on `main` it also packs and pushes both packages to the `pondhawktech` GitHub Packages feed and uploads the `.nupkg`s as an artifact.
- **`publish.yml`** — manual `workflow_dispatch` that promotes a build's artifacts to **NuGet.org** (uses the org-level `NUGET_ORG_API_KEY`).

## History

Extracted from the [pondhawktech/tools](https://github.com/pondhawktech/tools) monorepo. `Pondhawk.Logging` was consumed there by `Pondhawk.Api` (which stays in `tools` and now references it as a NuGet package). `Pondhawk.Logging.Watch` is consumed by the [pondhawk/watch-server](https://github.com/pondhawk/watch-server) client.

## License

MIT — see [LICENSE](LICENSE).
