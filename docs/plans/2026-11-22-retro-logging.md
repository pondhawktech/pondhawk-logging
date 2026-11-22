# Plan: retro logging

Status: **IMPLEMENTED** — 2026-11-22 (`feat/retro-logging`). Changes from the plan during implementation are
marked *Implemented:*.

## Why

A Debug event that the switch drops is lost. When a request fails, Watch shows the error but not the
steps that led to it. To see those steps, an operator turns the switch to Debug and waits for the failure
to happen again. Some failures do not happen again.

Retro logging keeps the dropped Debug events of a unit of work in memory, and sends them only when that
unit of work logs an Error. A unit of work that succeeds sends nothing extra.

The diagnostic probe is the opposite case. The probe decides at the start of a request to log everything.
Retro logging decides at the end. Both are marked the same way: a custom property on the unit of work's
`Activity`.

## Decisions — 2026-11-22

- **Watch only.** CloudWatch never takes Debug (fixed Information floor), and Journald has its own floor.
- **Opt-in is per unit of work, not per category or switch.** The question is "does this endpoint do
  important work", not "is this category noisy". An endpoint opts in; every category it logs through is
  captured for that request. No switch or Watch Server change.
- **Debug only, never Trace, payload content dropped** — the scope below.
- **A bonus, not a guarantee.** The gain is seeing how far a unit of work got before it failed. Where
  catching more would add a mechanism, the plan leaves it out and states the gap.

## Scope

The cost of retro logging is paid at the call site. `IsEnabled` must return true for an event that will
only be buffered, so the call site does its work even if the buffer is discarded. The scope keeps that work
cheap:

| Call | Level | Call-site work | Retro |
|---|---|---|---|
| `EnterMethod` enter/exit | Debug | a fixed string, the nesting depth | captured |
| `Inspect` | Debug | one `Convert.ToString` | captured |
| `LogDebug("…", args)` | Debug | boxes the arguments | captured |
| `LogJson/LogSql/LogXml/LogYaml/LogText` | Debug | none (the caller has the string) | captured, **content dropped** |
| `LogObject` | Trace | JSON serialization | **not captured** |
| anything at Trace | Trace | — | not captured |

- **Debug only.** `IsEnabled` sees only the category and the level, so the level is the only way to
  separate the calls. `LogObject`, the one call that serializes, is at Trace by default.
- **Payload content is dropped** from a buffered event. The title stays ("Query", "Response").
- **Known gap:** `LogObject(LogLevel.Debug, …)` serializes for retro capture. It is an explicit choice by
  the caller and rare. Document it; add no mechanism.
- **Remaining cost:** `Post` formats each captured event (`entry.ToString()`) on the calling thread, also
  when the buffer is discarded later. That is a string format per Debug event, with no serialization, and
  only inside a unit of work that opted in.
- **Unchanged:** CloudWatch, Journald, the deprecated console, the Winston transport, the Watch wire format,
  the switch model.

---

## 1. The marker (contract by name)

The Activity custom property `Pondhawk.RetroLogging`. Same shape as `Pondhawk.DiagnosticProbe`: set on the
current `Activity`; active when that Activity or a parent carries it.

- **`Pondhawk.Logging`:** public `RetroLogging` — `Begin()` (false when there is no current `Activity`) and
  `IsActive`, as `DiagnosticProbe`. For units of work that are not HTTP requests: a background job, a queue
  message handler.

  ```csharp
  using var activity = source.StartActivity("ImportBatch");
  RetroLogging.Begin();
  ```

- **`Pondhawk.Logging.AspNetCore`:** internal `RetroContract` (its copy of the name, with `Begin()`), and the
  endpoint opt-in:

  ```csharp
  app.MapPost("/orders", PlaceOrder).WithRetroLogging();   // minimal APIs

  [RetroLogging]                                            // controllers / actions
  public IActionResult Refund(…) { … }
  ```

  `RetroLoggingAttribute` (endpoint metadata) and `WithRetroLogging()` live in this package, since the
  package does not reference the API. `DiagnosticsEnrichmentMiddleware` reads
  `context.GetEndpoint()?.Metadata.GetMetadata<RetroLoggingAttribute>()` and calls `RetroContract.Begin()`.
  Enrichment runs after routing and authentication, so the endpoint is known.
  **Limit — decided 2026-11-22, the conservative approach:** the marker is set in enrichment only. Events
  logged before it (the monitor's "Begin" line, routing, authentication) are not captured. The trail is a
  bonus: showing how far the request got before it failed is the gain, not every last detail. Not added,
  each for that reason:
  - setting the marker in the monitor when routing has already run;
  - the monitor's End line for a 5xx or an exception as a flush trigger (a request whose exception no
    handler logs at Error sends no trail);
  - an end-of-request signal from the monitor to release the buffer (`IdleTimeout` releases it).

- **`Pondhawk.Logging.Watch`:** internal `RetroContract` (the name and `IsActive`).
- **`ContractTests`** in the Watch and AspNetCore test projects check each copy against the API, as for the
  probe.

## 2. The filter (Watch)

Today `AddWatch` registers one global filter: switch level, or the probe. A global filter that also says
yes to Debug would send Debug to every provider without a scoped rule (for example a plain console next to
Watch). So the retro clause must reach only Watch.

1. Give Watch its own provider type, `WatchLoggerProvider`, a subclass of
   `ZLoggerLogProcessorLoggerProvider`, as CloudWatch has (`CloudWatchLoggerProvider`) for the same reason.
   Register it in place of `AddZLoggerLogProcessor`. *Implemented:* no `[ProviderAlias]`, so no new
   configuration name appears.
2. Keep the global filter as it is. It still gates every other provider.
3. ~~Add a filter scoped to `WatchLoggerProvider`~~ *Implemented:* extend the one global filter, which takes
   the provider name (`AddFilter((provider, category, level) => …)`), and pass Debug for a marked unit of work
   only when the provider is `WatchLoggerProvider`. A scoped rule would outrank the application's category rules
   (`Logging:LogLevel:<category>`) for Watch, where today they outrank the switch filter. The clause:

   ```text
   level >= switch.Level
   || (level < None && ProbeContract.IsActive)
   || (level == Debug && RetroContract.IsActive)
   ```

   The retro clause runs last, so a category the switch
   already passes at Debug pays nothing for it, and an unmarked request pays one walk of the Activity chain
   per dropped Debug call — the same as the probe check before it.

**Check:** inside a marked Activity, `IsEnabled(Debug)` is true and `IsEnabled(Trace)` is false for a
category at Warning; a second, unscoped provider does not receive the Debug event.

## 3. The processor (Watch)

New internal type `RetroBuffers`, owned by `WatchLoggerProcessor`. `Post` decides per event, on the calling
thread:

1. **Retro-only event** — Debug, below the switch level, not probed, `RetroContract.IsActive`:
   - Convert as today (`ConvertEntry`), then clear `Payload` and set `Type` to none.
   - Append to the buffer for its correlation id. Do not write to the channel.
2. **Error or Critical event** with a correlation id that has a buffer:
   - Remove the buffer, write its events to the channel in order, then write the error.
   - The events keep their own `Occurred`, so Watch shows them in time order before the error.
3. **Any other event:** to the channel, as today.

**Limits** — `WatchOptions.Retro` (`RetroOptions`), clamped at construction:

- `MaxEventsPerUnit` (default 100; *implemented:* lowered from 200 to bound worst-case memory) — a ring buffer; the oldest event is dropped. The trail nearest the error
  is the most useful.
- `MaxUnits` (default 256; *implemented:* lowered from 1000) — at the cap a new unit of work is not buffered. No eviction: the trail is a
  bonus, and a full table means a burst that the idle sweep will clear.
- `IdleTimeout` (default 2 minutes) — the flush loop removes buffers not touched for that time. Watch has no
  signal for the end of a unit of work, so idle time is the end.
- Count drops and evictions (`RetroDroppedEventCount`), as `DroppedEventCount` does.

**Thread safety:** `ConcurrentDictionary<string, Buffer>`; a lock per buffer. `Post` must never throw.

**Marking:** a flushed retro event gets the tag `Retro`, so a reader can tell it was not live — as the probe
uses `Diagnostics`.

**Delivery:** flushed retro events are Debug, so during an outage or while unbound they are not held in the
critical buffer (Warning and above only). The error itself is held as today. Accepted.

**Dispose:** discard the buffers.

## 4. Tests

- **Watch:** §2's check; no marker, no change from today; Trace never enabled by retro. Processor: a success
  sends nothing extra; an error sends the trail first, in order, tagged `Retro`, payload content dropped,
  titles kept; enter/exit pairs both arrive (`MethodLogger` logs exit whenever entry was logged); two
  correlation ids do not mix; each cap drops what it should and counts it; idle buffers are removed; a
  probed request is sent live, not buffered. One test through `Pondhawk.Logging` calls (`EnterMethod`,
  `Inspect`, `LogSql`, `LogObject`): `LogObject` absent, `LogSql` title without content.
- **AspNetCore:** an endpoint with `WithRetroLogging()` / `[RetroLogging]` marks the request's Activity; one
  without does not; a request with no matched endpoint does not.
- **Pondhawk.Logging:** `RetroLogging.Begin()` and `IsActive`, as the `DiagnosticProbe` tests.
- **ContractTests** for both copies of the name.

## 5. Documentation

- Watch `README.md` and `CLAUDE.md`: "Retro logging" — what is captured, the call-site cost, the limits,
  the `LogObject(LogLevel.Debug, …)` gap, the new provider type.
- AspNetCore `README.md`: the endpoint opt-in and the enrichment limit.
- Pondhawk.Logging `README.md` and `CLAUDE.md`: `RetroLogging.Begin()` for non-HTTP units of work.
- Root `CLAUDE.md`: the marker in the contract list; one line per package.

---

## Addendum — 2026-11-22: the trail moves onto the Activity

5.0.1 shipped §3 as planned: a table of trails by correlation id, capped at `MaxUnits` and swept after
`IdleTimeout`. Watch has no signal for the end of a unit of work, so the trail of every request that **succeeded**
stayed until the sweep: two minutes, long enough to reach gen 2 and die there. And finished requests filled the
table, so at about 2 requests a second to a marked endpoint most new requests kept nothing.

Fix: the trail is a custom property on the marked `Activity` (`Pondhawk.RetroLogging.Trail`, Watch's own, not
part of the contract). It lives exactly as long as the unit of work and is collected with it. No table, no cap
on units of work, no sweep timer. `MaxUnits` and `IdleTimeout` stay on `RetroOptions`, `[Obsolete]` and ignored,
so code that set them still compiles.

Measured with `benchmarks/Pondhawk.Logging.Benchmarks` (BenchmarkDotNet short job; a request of 23 Debug calls
below the switch):

| Request | 5.0.1 time | 5.0.1 alloc | Fixed time | Fixed alloc |
|---|---:|---:|---:|---:|
| Unmarked, 5.0.0 filter | 1.06 µs | 1.23 KB | 1.05 µs | 1.23 KB |
| Unmarked | 1.26 µs | 1.23 KB | 1.37 µs | 1.23 KB |
| Marked, succeeds | 11.3 µs | 7.12 KB | 11.7 µs | 7.8 KB |
| Marked, fails | 14.0 µs | 7.35 KB | 31.2 µs | 10.1 KB |

5.0.1's "fails" looks cheaper only because its table was full: the trail was not kept and not sent. The fixed
numbers are what a failing request actually costs, its trail's serialization and post included.
