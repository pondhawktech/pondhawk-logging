# Plan: context without an exception, and other small Fabrica.Watch gaps

Status: **PLAN** — 2026-10-16. Part 1 implemented (`feature/context-without-exception`); the survey (§2) not started.

## Why

`ErrorWithContext(cause, context, message)` is the only way the Pondhawk.Logging API puts a context
object on an event, and it needs an exception. Fabrica.Watch also had `WarningWithContext`, and
fabrica-one's copy added `…WithContext(context, message)` overloads with no exception. Pondhawk.Logging
has no equivalent, so the migration analyzer (PHLM008) splits each such call into two events: a
`LogObject(level, "Context", context)` and then the message.

The fabrica-one migration (kampilan/fabrica-one#2) hit this four times in
`Fabrica.One.Support.Aws/.../TargetGroupPipelineStep.cs`: two warnings and two errors that have no
exception. In CloudWatch each one is now two events where it used to be one:

```json
{"Level":"Warning","Title":"Context","Payload":{"InstanceId":"i-…","TargetGroups":[…]}}
{"Level":"Warning","Title":"Nothing was deregistered. Skipping the drain wait"}
```

This costs three things:
- **Reading:** a search for the message finds an event with no context. The context is in the event
  before it, titled only "Context".
- **Querying:** `Context.InstanceId` no longer finds these events.
- **Alerting:** a `{ $.Level = "Error" }` metric filter counts each of the errors twice.

Two pieces of work:

1. **Context without an exception, and at Warning** — new overloads in `Pondhawk.Logging`, with the
   analyzer emitting them.
2. **A survey of other small gaps** between Fabrica.Watch and Pondhawk.Logging. It produces a list for
   decision and implements nothing.

---

## 1. `WarningWithContext` and exception-less `ErrorWithContext` (`Pondhawk.Logging`)

**API.** New extension methods on `ILogger` in `LoggingExtensions`, next to the existing
`ErrorWithContext(cause, context, message)`:

```csharp
logger.ErrorWithContext(context, message);           // Error, no exception
logger.WarningWithContext(cause, context, message);  // Warning, with an exception
logger.WarningWithContext(context, message);         // Warning, no exception
```

**Mechanics.** Each method does what `ErrorWithContext` does today:
- It gates on `IsEnabled` first, so a dropped level serializes nothing.
- It serializes the context with `JsonObjectSerializer` and attaches it as
  `LogPropertyNames.ErrorContext` on a `LogState`.
- The exception, when one is passed, goes to `ILogger.Log`.

The property keeps its name `Pondhawk.ErrorContext`, because it is the contract with the providers.
Document that it now also comes at Warning.

**Overload resolution — decided 2026-10-16: an extra overload, not a runtime check.** Without one,
`ErrorWithContext(cause, "message")` with two arguments binds to `(object context, string message)`: the
exception becomes the context and is serialized. So each level also gets `(Exception cause, string
message)`, which the compiler prefers over `object` for an exception. It logs the exception with no
context, as `LogError(cause, message)` would, but without treating the message as a template:

```csharp
logger.ErrorWithContext(cause, message);    // Error, the exception, no context
logger.WarningWithContext(cause, message);  // Warning, the exception, no context
```

That makes five new methods. A context typed `object` that holds an exception at run time still goes
in as context; there is no runtime check.

**Providers: no change expected. Prove it with tests.**
- **CloudWatch:** `CloudWatchEventFormatter.FormatWhole` reads the context before it looks at the
  exception, so `Context` is written without one. Add a formatter test: a Warning with context and no
  exception gives `Context` as an object and no `Exceptions`.
- **Watch:** `WatchLoggerProcessor` already sends context-without-exception as a Json payload
  (`"Context without an exception"` branch, about line 250). Add a test at Warning.
- **Console:** writes the title only, unchanged.

**Tests (`Pondhawk.Logging.Tests`).** For each new overload:
- the level is right
- the context is attached as JSON
- the exception, or none, reaches `Log`
- nothing is serialized when the level is off

**Docs:**
- `src/Pondhawk.Logging/README.md` and `src/Pondhawk.Logging/CLAUDE.md` (the API list and the
  `Pondhawk.ErrorContext` property note)
- the root `README.md` example
- the API bullet in the root `CLAUDE.md`

### Analyzer (`Pondhawk.Logging.Migration`, PHLM008 and PHLM020)

- **`ContextCall.CarriesOver`:** Error and Warning, with or without an exception, with a string
  message, carry over with the using swap. Add `WarningWithContext` to the names `UsingSwap` and
  `CarriedOver` treat as meaning the same.
- **A non-string message** is rewritten as today: `$"{…}"`, or `ex.Message` for an `ErrorWithContext`
  with no message.
- **`InfoWithContext`, Trace and Debug** keep the split `LogObject` + `LogX`, unless the survey (§2)
  decides otherwise.
- **The expression-bodied case** (the TODO marker for lost context) is no longer needed for
  Error/Warning; it stays for the other levels.
- **PHLM020:** `CreateEvent(Level.Warning, title, ex, context)` becomes `WarningWithContext`, as Error
  does now.
- **Tests:** update `PayloadAndReportTests`:
  - `WarningWithContext_logs_the_context_then_the_warning`
  - `A_context_call_without_an_exception_logs_the_context_then_the_message` (Error and Warning rows
    carry over; Info stays split)
  - the PHLM020 Warning case
  - `WholeFileTests` if it covers context
- **Guide:** update `MIGRATING-FROM-FABRICA-WATCH.md` (the table at about line 196, and line 219).

### Versions

- `Pondhawk.Logging` 3.2 → **3.3**
- `Pondhawk.Logging.Migration` 1.0 → **1.1**

The providers are unchanged apart from tests.

### Follow-up in fabrica-one (separate PR there, after publishing)

1. Bump `Pondhawk.Logging` to the published 3.3 build.
2. Make the four split calls in `TargetGroupPipelineStep` single calls again (about lines 221, 334, 528
   and 628 on master): two `WarningWithContext(context, message)` and two
   `ErrorWithContext(context, message)`.

---

## 2. Survey: other small gaps between Fabrica.Watch and Pondhawk.Logging

**Question.** Where else does a Fabrica.Watch call have no one-call equivalent in Pondhawk.Logging,
when a small extension method in the existing pattern would give one?

**Sources:**
- Fabrica.Watch as published: `~/repositories/fabrica-thin`, `Fabrica.Watch/` at `2f23540`, especially
  `LoggerExtensions.cs`, `WatchExtensions.cs` and `ILogger.cs`
- fabrica-one's extended copy: `git -C ~/repositories/fabrica-one show 269246c:Fabrica.Watch/…`
  (removed in `35e6041`)
- the analyzer's own record of where it falls short, in `Pondhawk.Logging.Migration`:
  - every path that writes a `RewriteContext.Marker` TODO
  - every rule that rewrites one call into two
  - every report-only rule (PHLM010, PHLM020–024)
- the stub `test/Pondhawk.Logging.Migration.Tests/Stubs/FabricaWatch.cs`

**What counts as a simple gap.** All of these must hold:
- a public Fabrica.Watch member
- applications plausibly call it
- the migration today drops information, splits it into several events, or leaves a TODO
- a fix is a small extension method or overload in the existing `LoggingExtensions` pattern, with no
  new provider behaviour

**Already decided, not to be reopened:**
- level-name aliases (`Debug`, `Info`, … on `ILogger`)
- `CreateEvent`/`LogEvent` in Pondhawk.Logging (PHLM020 rewrites them instead)
- `LogObject` without a level stays at Trace
- the MongoDB sink, a testing package, and a hook to feed hosts' correlation ids
  (`2026-10-15-fabrica-watch-migration-support.md`, "Not in scope")

**Output.** A table added to this plan, one row per candidate, with these columns:
- the Fabrica.Watch member
- what the migration does with it today
- the proposed Pondhawk.Logging equivalent
- a recommendation (add / leave)

The survey implements nothing. Each "add" is decided before it is built, and then goes in as its own
section of this plan.

### Survey results

*(To be filled in.)*
