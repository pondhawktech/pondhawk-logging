<p align="center">
  <img src="pht-small-logo.png" alt="Pondhawk.Logging" width="120" />
</p>

<h1 align="center">Pondhawk.Logging</h1>

<p align="center">
  A Serilog-based structured logging API (method tracing, object/payload logging, [Sensitive] masking) plus a Watch Server provider — sink, batching, and dynamic switch-based level control.
</p>

<p align="center">
  <a href="https://github.com/pondhawktech/pondhawk-logging/actions/workflows/build.yml"><img src="https://github.com/pondhawktech/pondhawk-logging/actions/workflows/build.yml/badge.svg" alt="Build" /></a>
  <img src="https://img.shields.io/badge/.NET-10.0-512bd4" alt=".NET 10" />
  <img src="https://img.shields.io/badge/license-MIT-blue" alt="MIT License" />
  <a href="https://www.nuget.org/packages/Pondhawk.Logging"><img src="https://img.shields.io/nuget/v/Pondhawk.Logging?label=Logging" alt="Pondhawk.Logging on NuGet" /></a>
  <a href="https://www.nuget.org/packages/Pondhawk.Logging.Watch"><img src="https://img.shields.io/nuget/v/Pondhawk.Logging.Watch?label=Logging.Watch" alt="Pondhawk.Logging.Watch on NuGet" /></a>
</p>

Two packages, both `net10.0` and fully standalone (no dependency on other Pondhawk packages):

| Package | Description |
|---------|-------------|
| [**Pondhawk.Logging**](src/Pondhawk.Logging/README.md) | The Serilog-based structured logging API (method tracing, object/typed-payload logging, `[Sensitive]` masking) + the `ILoggerSource` acquisition abstraction. **No sink or transport** — providers build on it. |
| [**Pondhawk.Logging.Watch**](src/Pondhawk.Logging.Watch/README.md) | Watch Server provider for `Pondhawk.Logging` — a Serilog sink with Channel-based batching, dynamic switch-based level control, and a switch-aware `ILoggerSource`. |

## Installation

```bash
dotnet add package Pondhawk.Logging
dotnet add package Pondhawk.Logging.Watch   # only if you deliver events to a Watch Server
```

## Pondhawk.Logging

Structured logging as extensions on Serilog's `ILogger` (`using Pondhawk.Logging;`):

```csharp
using var _ = logger.EnterMethod();          // entry/exit + elapsed, disposable scope

logger.Inspect(nameof(orderId), orderId);    // "orderId = 4271" at Debug
logger.LogObject("order", order);            // serialize an object to a JSON payload
logger.LogJson("payload", jsonString);       // typed payload with syntax-highlight hints
```

`[Sensitive]` masks a property when an object is serialized. Also included: `CorrelationManager`, the `PayloadType` enum, the `JsonObjectSerializer`, and the public `LogPropertyNames` contract that sinks read.

### ILoggerSource

The single seam an app injects to obtain category-scoped loggers, independent of the provider underneath:

```csharp
public interface ILoggerSource
{
    ILogger CreateLogger<T>();       // returns Serilog.ILogger
    ILogger CreateLogger(Type source);
    ILogger CreateLogger(string category);
}
```

`SerilogLoggerSource` is the canonical default (`root.ForContext(SourceContext, category)`). Swap in a provider's source — or your own — and handlers are unchanged.

## Pondhawk.Logging.Watch

Deliver events to a Watch Server and make the logging API switch-aware:

```csharp
using Pondhawk.Logging.Watch;
using Serilog;

// Recommended — the Watch Server controls levels via switches
Log.Logger = new LoggerConfiguration()
    .UseWatch("http://localhost:11000", "MyApp")
    .CreateLogger();
```

`UseWatch(..., out SwitchSource)` exposes the switch source so the root can share one instance with a `WatchLoggerSource` — payloads for switch-dropped categories are never serialized. Events are batched over an unbounded Channel and sent as MemoryPack+Brotli, with a circuit breaker for HTTP resilience. See the [package README](src/Pondhawk.Logging.Watch/README.md) for switching, `ILoggerSource` wiring, and the event model.

## Repository Layout

```
src/Pondhawk.Logging/          The logging API + ILoggerSource (net10.0)
src/Pondhawk.Logging.Watch/    Watch Server provider — sink + switching (net10.0)
test/Pondhawk.Logging.Tests/
test/Pondhawk.Logging.Watch.Tests/
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
