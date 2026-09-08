# Feature requests for Pondhawk.Logging

From the Pondhawk One team. These came out of a gap analysis for replacing
`Fabrica.Watch` with `Pondhawk.Logging` + `Pondhawk.Logging.Watch` in
`pondhawktech/pondhawk-one`. Measured against `Pondhawk.Logging` 2.0.7.

**Most of the swap already works.** `EnterMethod` and `GetLogger` on any object,
`Inspect`, call-depth nesting, correlations, `PayloadType`, and the switch-based
level control all map cleanly onto what we have today, and the `[Sensitive]`
attribute is something we did not have. The four items below are what we cannot
do at all, plus one naming question. Items 1-4 block the swap; item 5 is a
preference we are happy to solve on our side.

---

## 1. Payload logging at a caller-chosen level

**Today:** every payload method — `LogJson`, `LogSql`, `LogXml`, `LogYaml`,
`LogText`, both `LogObject` overloads — is hard-coded to `LogLevel.Trace` and
returns early unless Trace is enabled.

**Why that is a problem:** the payload attached to a *failure* is the most
valuable payload there is, and it is exactly the one that cannot be emitted
above Trace. In Pondhawk One three sites attach a payload to an Error or Info
event:

- the malformed mission plan's JSON, on the error that reports it as malformed
- the JSON schema the plan failed against
- the result object on a startup failure

Under the current API those become Trace events. On any host running at Debug or
above — which is every production host — the error message survives and the
content needed to diagnose it silently disappears. It is a quiet failure, not a
compile error, which is what makes it worth fixing before anyone depends on it.

**Requested:** level-taking overloads, keeping the existing signatures as
Trace-defaulting so nothing breaks.

```csharp
public static void LogJson(this ILogger logger, LogLevel level, string title, string? json);
public static void LogYaml(this ILogger logger, LogLevel level, string title, string? yaml);
public static void LogText(this ILogger logger, LogLevel level, string title, string? content);
public static void LogSql (this ILogger logger, LogLevel level, string title, string? sql);
public static void LogXml (this ILogger logger, LogLevel level, string title, string? xml);
public static void LogObject<T>(this ILogger logger, LogLevel level, string title, T value);
```

**Also worth a thought while you are in there:** the existing defaults may be a
level too low. `Fabrica.Watch` emitted `LogJson` and `LogYaml` at **Debug**, and
only `LogObject` at Trace. Consumers moving across will find their JSON and YAML
payloads have quietly dropped a level even where they never asked for one.

---

## 2. `ErrorWithContext` — the wire model has the field, nothing fills it

**Today:** `Pondhawk.Logging.Watch.LogEvent` already carries an `ErrorContext`
property, but no API in `Pondhawk.Logging` populates it. As far as we can tell
the field is unreachable from consumer code.

**Requested:**

```csharp
public static void ErrorWithContext(this ILogger logger, Exception cause, object context, string message);
```

serialising `context` into `LogEvent.ErrorContext`.

**Why:** we have 12 call sites that attach the state surrounding a failure —
the EC2 instance id on a target-group lookup failure, the ARN and instance id on
a deregistration failure, the appliance and build on an installation failure.
Folding that into the message string loses its structure, and a separate
`LogObject` call detaches it from the exception it explains.

---

## 3. A supported way to change the Watch destination at runtime

**Today:** `AddWatch(serverUrl, domain)` fixes both at registration and builds an
`HttpClient` with a fixed `BaseAddress`. `LoggingFactoryLocator.SetFactory`
throws `InvalidOperationException` on a second call. `WatchSwitchSource` changes
*levels* at runtime but never the destination.

**Why we need it:** Pondhawk One's agent runs on a baked AMI and reads no EC2
user-data. Its entire configuration surface is fixed at image bake time except
for one channel: the mission plan the orchestrator writes, which carries the log
domain and event-store address. Today the agent rebuilds its logging factory
when a plan names a destination it is not already using — starting the new
factory before swapping and stopping the old one after, so events in flight are
not lost. Without an equivalent, an agent can only ever log where the image was
baked to log, and the orchestrator loses the ability to direct a fleet's events.

**Requested:** any supported mechanism, we are not attached to a shape. Two that
would work for us:

- something resolvable from DI, e.g. `IWatchDestination.Rebind(serverUrl, domain)`
- `WatchOptions` accepting a provider delegate for server URL and domain, re-read
  per batch rather than captured once

The one requirement is that a rebind must not drop already-buffered events.

**If you would rather not support it,** say so explicitly in the README — that
the destination is fixed for the life of the process — and we will redesign
around it rather than discover it in production.

---

## 4. The set-once locator makes the ordinary NUnit pattern fail

**Today:** `LoggingFactoryLocator.SetFactory` may be called once per process and
throws on the second call. `Reset()` exists but is `internal`.

**Why that is a problem:** NUnit's `[OneTimeSetUp]` runs once per **fixture**,
not once per assembly. The natural pattern — a shared abstract test base that
stands logging up in `[OneTimeSetUp]` — therefore calls `SetFactory` once per
fixture. Pondhawk One has 10 fixtures in one test assembly and 10 in another,
each inheriting such a base. The second fixture to run would throw, with a
message about startup that does not obviously point at the test harness.

`Fabrica.Watch` tolerated this because building a factory swapped it.

**Requested:** any one of these, your call —

- a public, plainly-named test hook: `LoggingFactoryLocator.ResetForTesting()`
- make `SetFactory` last-wins, or idempotent when handed the same factory
- keep set-once, and document the `[SetUpFixture]` assembly-level pattern in the
  README as *the* supported way to stand logging up in tests

Any of the three is fine. What does not work is the current combination: a
set-once contract, an internal reset, and no documented test pattern.

---

## 5. Level-name convenience extensions — your call, we lean no

`Fabrica.Watch` exposed `Debug`, `Info`, `Warning`, `Error` and the `*Format`
variants directly on the logger. `Microsoft.Extensions.Logging` spells these
`LogDebug`, `LogInformation`, `LogWarning`, `LogError`. That is roughly 350 call
sites for us.

This is naming, not function, and we can shim it on our side. We are flagging it
only so the decision is yours: **our own inclination is that a package built on
MEL should not add a second spelling for something MEL already provides**, and
that we should take the churn instead.

---

## Not requested

For scope: we do not need a realtime/desktop viewer. `Fabrica.Watch.Realtime`
drove a commercial SmartInspect viewer; we are dropping that requirement, and
losing the commercial dependency is a benefit to us.

## Notes

- Measured against `Pondhawk.Logging` 2.0.7 and `Pondhawk.Logging.Watch` as
  cached locally.
- The packages target `net8.0`; Pondhawk One is `net10.0`. Not an issue.
- Item 3 is the only one that is genuinely about Pondhawk One's architecture.
  Items 1, 2 and 4 look to us like gaps any consumer would eventually hit.
