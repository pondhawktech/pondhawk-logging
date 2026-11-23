# Retro trail in the error payload, and a per-switch Retro flag

Status: **IMPLEMENTED** — 2026-11-23 (`feat/retro-trail-in-error-payload`). Changes from the plan during
implementation are marked *Implemented:*.

Supersedes the delivery in `2026-11-22-retro-logging.md` §3 (separate `Retro`-tagged events) and its decision
"No switch or Watch Server change". The marker (§1), the AspNetCore opt-in and the Watch-only filter (§2) stay.

Server half: `watch-server/docs/plans/2026-11-23-switch-retro-flag.md`.

## Problem

Today an Error in a marked unit of work sends its trail as up to 100 separate Debug events tagged `Retro`, ahead
of the error. The developer wants one thing: the last few Debug lines before the failure, next to the exception.
Separate events must be found by correlation id and read beside the error. And only code can opt in: an operator
who sees failures in an area cannot turn a trail on without a release.

## Decisions — 2026-11-23

1. **Opt-in: code marks and a switch flag.** `[RetroLogging]` / `WithRetroLogging()` / `RetroLogging.Begin()`
   stay. A switch gets `Retro` (bool, from Watch Server): Debug in its categories is trailed in any unit of work.
2. **Holder:** the marked `Activity` when there is one, else the **outermost local `Activity`** (walk `Parent` to
   null) — the request, not a child span such as an outgoing `HttpClient` call. No `Activity`: no trail (gap).
3. **Never at `None`.** A category whose switch is `None` collects nothing for retro, code marks included — an
   inactive domain's catch-all switch turns retro off.
4. **Ring size:** `RetroOptions.MaxEventsPerUnit`, default lowered **100 → 20**. Same name, same clamp (1–10,000).
5. **Trigger:** Error and Critical, with or without an exception.
6. **Shape:** a `--- Trail ---` block in the existing single payload slot: Context, Trail, Exception. No wire
   change, no server change (Watch Server shows the payload as text; nothing parses the banners).
7. **Slot taken:** an Error with its own payload (`LogJson(LogLevel.Error, …)`) goes out as today; its trail is
   discarded. Stated gap.
8. **Repeat errors:** each error carries the lines since the previous one (the ring is cleared on attach).
9. **Line:** offset from the first line, last category segment, indentation by nesting, the message (payload
   titles only, as today).
10. **The separate `Retro` events are removed.** `RetroContract.Tag` goes.
11. **CloudWatch and Journald:** not in this plan.

## 1. The switch flag (client)

- `SwitchDto.Retro` (bool, default false), `SwitchDef.Retro`, `Switch.Retro` (`init`). `WatchSwitchSource.ApplyAsync`
  copies it. An old server sends none: false, today's behavior. An old client ignores the field.
- `SwitchSource.LevelFor` (PR #52) becomes `GateFor(category)` returning a cached `(LogLevel Level, bool Retro)`
  struct; same cache, same invalidation.
- `WhenMatched` gains an overload with `retro`, for local switch tables and tests.

**Depends on:** PR #52 (the level cache).

## 2. The filter

```text
level >= gate.Level
|| (level < None && ProbeContract.IsActive)
|| (level == Debug && provider == WatchLoggerProvider && gate.Level != None
    && (gate.Retro ? Activity.Current is not null : RetroContract.IsActive))
```

A category whose switch has `Retro` off and no mark in force pays what it pays today. With `Retro` on, the check
is one `Activity.Current` read — no walk.

## 3. Collection (processor)

`Post`, for Debug below the switch, not probed, switch not `None`:

- **Holder** = `RetroContract.MarkedActivity` ?? (switch `Retro` ? outermost local `Activity` : none). None: drop
  (the filter already let it through; this only happens when the switch changed between `IsEnabled` and `Post`).
  *Implemented:* with no holder the event is sent live, as before. Dropping it broke the processor used without the
  switch filter (the public constructor with `AddZLoggerLogProcessor`), where Debug below the switch was always sent.
- **Keep a line, not a `LogEvent`.** `RetroTrails` holds `TrailLine(long Timestamp, string Category, int Nesting,
  string Text)`: `Stopwatch.GetTimestamp()`, the last category segment, the nesting delta, the title (payload
  content dropped, as today). Smaller than a `LogEvent`, and nothing is converted that is not kept.
- Text capped at 500 characters per line, so a full ring is at most ~10 KB.
- Ring of `MaxEventsPerUnit`; the oldest goes; `RetroDroppedEventCount` counts it.
- Debug sent live (the switch already passes it) is not in the trail — it is already in Watch.

## 4. Delivery (processor)

On Error or Critical in a unit of work with a holder (`MarkedActivity` ?? outermost local `Activity`; release from
whichever carries a trail):

1. Release the trail (cleared; a later Debug starts a new one).
2. Compose the payload slot:

   | Event carries | Payload today | With a trail |
   |---|---|---|
   | exception | Text: exception | Text: Trail, Exception |
   | exception + context | Text: Context, Exception | Text: Context, Trail, Exception |
   | context only | Json: context | Text: Context, Trail |
   | neither | none | Text: Trail |
   | its own payload | that payload | that payload; trail discarded |

3. The block:

   ```text
   --- Trail (12 lines) --------------------------------
   +0.000s  Service      Entering PlaceOrder
   +0.001s  Service        subtotal = 30
   +0.022s  Repository     Query
   +0.031s  Repository     Exiting Load (8.94ms)
   +0.044s  Service        Step 3
   ```

   Offsets from the first line, 3 decimals. Indent two spaces per depth: the running sum of nesting deltas,
   clamped at 0 (the ring may start inside a method). An exit line is indented at its enter's depth. Header
   says how many lines, and "(N dropped)" when the ring overflowed.

`ComposeErrorPayload` grows a trail argument; banners in the existing style.

## 5. Removed

- Queuing kept events to the channel; `RetroContract.Tag` and the `Retro` tag.
- `RetroTrails` holding `LogEvent`s.

## 6. Tests

- **Switch:** `Retro` read from the wire; absent = false; in `GateFor`; dropped with the cache on `Update`.
- **Filter:** switch `Retro` on, Warning: `IsEnabled(Debug)` true inside any `Activity`, false outside one, false
  for a second provider, false at `None`; a code mark at `None`: false; `Trace` never.
- **Processor:**
  - an Error after three Debug lines carries one Text payload: Trail block, then Exception; nothing else queued;
  - with context: Context, Trail, Exception; context only: Text, Context then Trail; neither: Trail only;
  - its own payload: unchanged, trail discarded;
  - second error: only the lines since the first;
  - ring of 20 keeps the last 20 and says how many dropped;
  - offsets, last segment, nesting indentation (incl. a ring that starts inside a method), 500-char cap;
  - holder: a child `Activity` under the request trails onto the request; a marked `Activity` wins;
  - a successful unit of work sends nothing;
  - probed requests are sent live, as today.
- **Through the API:** `EnterMethod`, `Inspect`, `LogSql` (title only), `LogObject` (absent) in the trail.
- **Benchmark:** `RetroLoggingBenchmarks` gains "switch Retro, succeeds" and "switch Retro, fails"; the "fails"
  numbers drop, since one event goes out instead of 23.
  *Implemented* (short job): "Marked, fails" 31.2 µs → 10.1 µs. Allocation went **up**, 10.1 KB → 12.5 KB: the one
  payload string, its builder and its serialization cost more than 24 small events did. Sizing the builder once
  saved about 150 B; the rest was not pursued.

## 7. Documentation

- Watch `README.md` and `CLAUDE.md` "Retro logging": the switch flag, the holder, the payload block and its
  cases, the `None` rule, the slot-taken gap, the new default of 20, benchmark results.
- AspNetCore `README.md`: code marks still trail every category of the endpoint; a switch flag trails an area.
- Root `CLAUDE.md`: the Watch section's retro paragraph.
- Mark `2026-11-22-retro-logging.md` §3 as superseded by this plan.

## Release

`Pondhawk.Logging.Watch` minor. No change to `Pondhawk.Logging` or `Pondhawk.Logging.AspNetCore`.
Works with any Watch Server: an old one sends no `Retro` and the code marks behave as before, delivered the new way.
