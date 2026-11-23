---
name: pondhawk-logging
description: How to write log calls in .NET code that logs through Microsoft.Extensions.Logging with the Pondhawk.Logging extensions (EnterMethod, Inspect, LogObject, LogJson, ErrorWithContext). Use when writing or reviewing a request handler, API slice, endpoint, service, background job, queue consumer or typed HTTP client, or when adding, changing or reviewing logging in such code. Covers the method scope, step messages, values, levels, warnings, errors and sensitive data. Does not cover sink set-up, configuration or how a logger is acquired.
---

# Logging with Pondhawk.Logging

Code logs through the standard `ILogger` (`Microsoft.Extensions.Logging`) plus the `Pondhawk.Logging` extensions.
How the class gets its `ILogger` is not part of this pattern. The examples call it `logger`.

The goal: at Debug, the log of one call reads as a story — what it was asked, each step it took, what each step
found, and why it stopped. At Warning and above, each event is enough on its own to triage.

## 1. Open a method scope

Start each handler, endpoint, service method or job with a method scope. Use the scope as the logger for the
whole body.

```csharp
public async Task<IResult> Handle(ChargeDeviceRequest request, CancellationToken ct)
{
    using var log = logger.EnterMethod();      // "Entering Handle" ... "Exiting Handle (412ms)", both at Debug
    ...
}
```

- One scope per method that does real work. Do not open one in a trivial getter or a tight loop.
- Pass the scope (`log`) to a helper that the method calls, so the helper's events nest inside it. A helper
  takes `ILogger`, not `MethodLogger`.
- **Background job:** open the scope per pass, in the method that does one pass (`RunPass`), not in
  `ExecuteAsync`. A scope around a loop that runs forever never closes, and every pass would nest inside it.
  `ExecuteAsync` logs through the plain logger.

## 2. Log each step before it runs

Mark each step with a banner comment and one Debug line, then do the step.

```csharp
// *****************************************************************
log.LogDebug("Attempting to fetch Device");
var device = await db.Devices.SingleOrDefaultAsync(d => d.Uid == request.DeviceUid, ct);
```

- Start the message with **"Attempting to"**, then a verb and the thing: `"Attempting to save changes"`,
  `"Attempting to send message to SQS"`. Keep it short — a verb and a thing, about 3 to 8 words after
  "Attempting to". Detail goes in `Inspect` or `LogObject`.
- The message is a constant string with no holes. Put values in `Inspect` or `LogObject` (section 3).
- One step = one unit of work: a query, a call to another service, a mapping, a save, a publish — and
  **in-memory work that decides the outcome** (validating rows, matching items, pricing).
- **A step done for each item of a loop:** write the step line once, before the loop. Inside the loop, log each
  item with `LogObject`. A step line repeated N times names no item, and a step line has no holes.
- **Per-item logging is for small, bounded loops** — a page, a batch, up to about 100 items. For bulk data (an
  import, a file, an unbounded query), log **counts only**, at Debug, after the loop: no per-item events, and no
  list of the rejected or skipped items. The response or the result carries those.

```csharp
// *****************************************************************
log.LogDebug("Attempting to release stale Holds");
foreach (var hold in stale)
{
    log.LogObject(nameof(hold), hold);
    await payments.Release(hold.ProviderHoldId, ct);
}
```

- **Items skipped inside a small loop** (not found, not eligible): collect them, and log them after the loop as
  one guarded object — `LogObject("Serials", new { notOnOrder, warrantyExpired })`. A skipped item may be only
  a string, with no object to log. In a bulk loop, log only how many were skipped.
- **A loop that saves each item** (so a retry cannot repeat work): one step line names both actions —
  `"Attempting to re-send each Notification and save its outcome"`.
- **A step that runs only sometimes:** put its step line inside the `if`, with the step.

## 3. Log the values that explain the step

| What | Call | Level |
|---|---|---|
| One scalar that stands alone: an id, a count, a flag, a computed amount | `log.Inspect(nameof(configId), configId)` | Debug |
| An entity, a DTO, a model, a result | `log.LogObject(nameof(device), device)` | Trace |
| Several related values that belong to no one object | `log.LogObject("Charge", new { fullAmount, previousAmount, creditAmount })`, guarded (section 9) | Trace |
| A payload sent on the wire (queue, MQTT, HTTP body) that holds no personal data | `log.LogJson("SQS Payload", json)` | Debug |
| SQL, XML, YAML, other text | `LogSql` / `LogXml` / `LogYaml` / `LogText(title, text)` | Debug |

- Log key inputs right after the scope opens, and each step's result right after the step.
- **Inputs that are separate parameters** (a minimal-API handler with no request object): `Inspect` a lone id,
  and group the rest into one guarded anonymous object.
- **Log an object whole, not property by property.** One `LogObject(nameof(request), request)` replaces a row of
  `Inspect(nameof(request.X), request.X)` calls. If the object holds a secret, mark that property `[Sensitive]`
  (section 8) — do not fall back to picking properties with `Inspect`.
- **Several loose values: one anonymous object, not several `Inspect` calls.** Group the values that explain one
  step into `new { … }` and log it with `LogObject`. Guard it (section 9): the object is built before the call.
- Use `nameof(x)` as the name or title, so it follows a rename. Where `nameof` gives an ambiguous name, use a
  literal: `log.Inspect("ErrorCode", ex.Code)`, not `nameof(ex.Code)`, which gives only `Code`.
- `LogObject` is Trace by design: it serializes the whole object. Do not raise it to Debug to see it more often.
- Do not build a string for these calls (`$"Id = {id}"`). Each call checks `IsEnabled` itself, and builds
  nothing when the level is off.

## 4. Explain each early exit

When the method stops or skips work for a normal reason, say so at Debug before you return.

```csharp
if (price.FullAmount <= 0)
{
    log.LogDebug("No price for this device - nothing to charge");
    return Results.Ok(ChargeResult.NotRequired);
}
```

**An expected failure is a result, not a log event.** Not found, validation failed, bad request, forbidden,
conflict: return the failure response. Never log it at Warning or Error. The response and the request
diagnostics already report it.

- Add a Debug line that says why **only when the status code does not explain itself**. A 404 right after its
  lookup needs none. A 409 because the account is frozen gets one.

```csharp
if (account is null)
    return Results.NotFound();                 // obvious: no line

if (account.IsFrozen)
{
    log.LogDebug("Account is frozen - refusing redemption");
    return Results.Conflict();
}
```

- **An exception that means an expected failure is an expected failure.** When a dependency rejects the
  caller's input by throwing (a wrong card code, an unknown id), catch it, log Debug without the exception, and
  return the failure response. It is not an Error.

```csharp
catch (IssuerException ex) when (!ex.IsTransient)   // the issuer rejected the card: the caller's input
{
    log.LogDebug("Issuer rejected the card - nothing to redeem");
    log.Inspect("ErrorCode", ex.Code);
    return Results.Problem(statusCode: 422, title: "Gift card cannot be redeemed");
}
```

## 5. Choose the level

| Level | Use for |
|---|---|
| Trace | `LogObject` |
| Debug | Method scope, steps, `Inspect`, early exits. Almost all of a handler's logging. |
| Information | Rare. The outcome of background or periodic work that an operator wants in the journal: `"Pruned {PrunedCount} activity-log rows older than {RetentionMonths} months"`. Not in a request handler. |
| Warning | The work goes on, but something is wrong: data that cannot be resolved, an external service that refused, a fallback taken, a setting that is missing. Also a **transient dependency failure that ends the request** (outage, timeout → 503): the client can retry, and an operator acts only if it repeats. |
| Error | An exception that is not transient, or a failure that makes the result wrong or leaves data inconsistent. |
| Critical | The process cannot continue. |

```csharp
catch (IssuerException ex) when (ex.IsTransient)
{
    log.WarningWithContext(ex, new { request.AccountUid, ex.Code },
        "Gift card issuer unavailable - redemption refused, client may retry");
    return Results.Problem(statusCode: 503, title: "Gift card issuer unavailable");
}
```

### Background work: the outcome of each pass

**Each item that fails:**

- **It will be retried** (next pass, next attempt): Warning — `"Re-send failed for Notification {NotificationUid}
  - retrying next pass"`.
- **It is given up** (last attempt, abandoned, staff must act): Error, saying what staff must do.

**End each pass with one summary event**, so an operator sees how much of the pass succeeded. Log it as well as
the per-item events: the summary is a different fact.

- **All items succeeded:** Information. `"Released {ReleasedCount} stale holds older than {MaxAgeHours} hours"`.
- **Any item failed or was given up:** Warning, with the counts and what happens to them.
- **Nothing to do:** Debug, as an early exit. An Information event every few minutes that says "nothing" is noise.

**A whole pass that throws** (the database is down): `ExecuteAsync` catches it, logs Error through the plain
logger with when it will try again, and goes on — `"Pass failed - retrying in {IntervalMinutes} minutes"`. The
cancellation at shutdown is not a failure: let it end the loop with no event.

```csharp
if (failed > 0)
    log.LogWarning("Released {ReleasedCount} of {StaleCount} stale holds; {FailedCount} failed - retrying them next pass",
        released, staleCount, failed);
else
    log.LogInformation("Released {ReleasedCount} stale holds older than {MaxAgeHours} hours",
        released, MaxAge.TotalHours);
```

### Queue consumers: one message at a time

A consumer handles one message and returns Ack, Retry or DeadLetter. Open a scope per message, log the message
with `LogObject` (personal data in it marked `[Sensitive]`), and choose the level by the cause:

| Outcome | Level |
|---|---|
| Stale or duplicate message, acked and ignored | Debug, as an early exit |
| Retry because of normal contention (another consumer got there first) | Debug: it resolves itself |
| Retry because a dependency failed | Warning |
| The last delivery attempt fails | Error, saying what staff must do |
| Dead-letter: the message will never succeed (unknown entity, bad payload) | Error, with the ids staff need |

A consumer has no pass, so it writes no summary event.

## 6. Write Warning and Error messages that stand alone

A Warning or Error is often read without the Debug events around it. Write it so it needs nothing else:

1. **What failed** — the operation, not the exception text.
2. **Which thing** — the ids and names, as named holes.
3. **What happens now** — the consequence: `- skipping`, `- continuing with remaining targets`,
   `- keeping the last known values`.

```csharp
log.LogWarning(
    "Occasion '{EventName}' (Id {EventId}) has an unresolvable {TargetKind} target '{TargetUid}' - skipping",
    ev.Name, ev.Id, target.Kind, target.TargetUid);

log.LogError(ex,
    "Dispatch failed for Occasion '{EventName}' (Uid {EventUid}) on {TargetKind} target {TargetAddress} - continuing with remaining targets",
    winner.Event.Name, winner.Event.Uid, winner.Target.Kind, winner.Target.Address);
```

- Use a message template with **named holes** (PascalCase), never string interpolation or `string.Format`.
- Do not repeat the exception's message in the template. The exception goes in the first argument.
- A hole may carry a computed value or a setting when it helps triage: `{MaxAgeHours}`, `{IntervalMinutes}`,
  `{FailedCount}`.

### Attach the state with `ErrorWithContext`

When the state that explains a failure is more than a few ids, attach it as an object instead of more holes.
It is serialized to JSON on the same event as the exception.

```csharp
catch (StripeException ex)
{
    log.ErrorWithContext(ex, new { request.DeviceUid, charge, ex.StripeError?.Code },
        "Stripe refused the checkout session request - no charge started");
    return Results.Problem(statusCode: 502, title: "Payment could not be started");
}
```

- `WarningWithContext` does the same at Warning; `LogWithContext(level, …)` at any level.
- The message is a **title, not a template**: holes are not filled. Put the values in the context object.
- The context can be given without an exception: `log.ErrorWithContext(context, "Totals do not match")`.

### Source-generated messages: `[LoggerMessage]`

`[LoggerMessage]` (`Microsoft.Extensions.Logging`) declares a log message as a `partial` method, and a source
generator writes its body at build time. The body checks `IsEnabled` first, takes typed parameters (no
`object[]`, no boxing) and uses a template parsed once. A hole with no matching parameter, or the reverse, is a
compiler error.

```csharp
internal sealed partial class TokenPruner(..., ILogger<TokenPruner> logger) : BackgroundService
{
    ...
    PruneFailed(logger, realm, (int)Interval.TotalMinutes, failure);

    [LoggerMessage(Level = LogLevel.Error, Message = "Pruning expired tokens failed for realm {Realm} - retrying in {IntervalMinutes} minutes")]
    private static partial void PruneFailed(ILogger logger, string realm, int intervalMinutes, Exception failure);
}
```

**Use it for operator events; use direct calls for the Debug story.**

- **May use `[LoggerMessage]`:** Information, Warning and Error events with a fixed template — the operator
  events of sections 5 and 6 — and any event on a hot path. If a codebase already uses it for these, follow it.
- **Do not use it for:**
  - **Step lines and early-exit lines.** One declared method per "Attempting to …" line doubles the size of a
    handler and moves the message away from the step it names. Write `log.LogDebug("Attempting to …")` at
    the step.
  - **`EnterMethod`, `Inspect`, `LogObject`, `LogJson` and the other payload calls.** It has no equivalent.
  - **Rich failure state.** Use `ErrorWithContext` / `WarningWithContext`; `[LoggerMessage]` has no context
    object, and more holes are not a substitute.
- **The rules do not change.** The message still says what failed, which thing and what happens now (section 6).
  The levels are those of section 5. Holes are named in PascalCase. A secret or personal value is never a
  parameter, because a parameter is a hole.
- Put the exception parameter last. The generator attaches it as the event's exception, not as a hole.
- Declare the methods `private static partial` at the end of the class. The class must be `partial`.
- Both styles can be used in one class: `[LoggerMessage]` for its operator events, direct calls and the
  Pondhawk extensions for its Debug story.

## 7. Catch only to handle

Log an exception **where it is handled**, once.

- **Handled** (the method recovers, retries, skips the item or returns a failure): log it there, at Error or
  Warning, with a message per section 6.
- **Not handled**: do not catch it. Let it go up to the code that handles it. The request diagnostics report an
  exception that leaves the request.

Do not write either of these:

```csharp
catch (Exception ex)
{
    log.LogError(ex, "{Message}", ex.Message);  // says nothing the exception does not say
    throw;                                       // and it is logged again further up
}
```

A `catch` that only logs and rethrows: delete it. A `catch` that handles: give it a message that says what
failed and what happens now.

**When only this place knows the consequence, handle it here.** Some failures mean more than the exception
says: the card is already spent at the issuer, but the credit was not saved. Do not log and rethrow. Catch it,
log `ErrorWithContext` with the ids needed to repair it and the consequence, and return the failure.

```csharp
catch (DbUpdateException ex)
{
    log.ErrorWithContext(ex, new { request.AccountUid, card.CardId, credited, RedemptionUid = redemption.Uid },
        "Card marked redeemed at issuer but credit not saved - manual reconciliation required");
    return Results.Problem(statusCode: 500, title: "Redemption could not be completed");
}
```

**When the consequence is true only after a later step, log the Error then.** The carrier refuses the return
label, but the claim is still saved without one and staff must create it. In the catch, log Debug and keep the
exception. After the save succeeds, log the one Error, because only now is "saved without a label" a fact.

```csharp
catch (CarrierException ex)
{
    log.LogDebug("Carrier refused the return label - saving the claim without one");
    labelFailure = ex;
}
...
await db.SaveChangesAsync(ct);

if (labelFailure is not null)
{
    log.ErrorWithContext(labelFailure, new { ClaimUid = claim.Uid, request.OrderUid },
        "Claim saved without return label - staff must create one");
    return Results.Accepted(value: response);
}
```

**Catch `Exception` only around one item's work**, in a loop where one item must not stop the others, or around
one pass of a job. There it is handled: counted, logged per the rules above, and the loop goes on. Let the
cancellation at shutdown through:

```csharp
catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
{
    failed++;
    log.LogWarning(ex, "Re-send failed for Notification {NotificationUid} - retrying next pass", notification.Uid);
}
```

Anywhere else, catch only the exception types you handle.

### A client that throws for its caller

A typed client (an `HttpClient` wrapper, an SDK wrapper) turns failures into exceptions for its caller. It does
not handle them, so it logs **Debug only**: the scope, the step, the typed request (`LogObject`, so
`[Sensitive]` applies), the status code and the typed result. On a failure, log Debug with the status code and
the error body, then throw. The caller logs Warning or Error where it handles the exception.

- Do not `LogJson` a raw request or response body that may hold personal data: `LogJson` ignores `[Sensitive]`.
  Log the typed request or result instead.
- A result that does not explain itself (`null` for "no service to that destination") gets a Debug "why" line.

## 8. Keep secrets out of the log

What is secret:

- **Secrets** — anything that grants access or money when read: passwords, tokens, API keys, PINs, card codes,
  card numbers, OTPs, connection strings.
- **Personal data** — names, postal addresses, email addresses, phone numbers, and **free text written by or
  about a customer** (a claim's description, a message body, a note): it can hold any of the others.
- **Not secret** — identifiers and amounts: uids, ids, provider ids (a payment provider's hold or session id),
  card ids, amounts, balances, currencies. Log them freely; they are what makes an event traceable.

How to keep them out:

- Mark each property that holds a secret or personal data with `[Sensitive]` (`Pondhawk.Logging`). `LogObject`
  then writes `Sensitive - HasValue: true/false` instead of the value.
- On a positional record, target the property: `record Redeem(Guid AccountUid, [property: Sensitive] string Pin)`.
- **An entity in your own codebase** that holds personal data gets `[Sensitive]` on those properties, like any
  other type.
- Only when you cannot change the type (a generated type, another library's type), log a guarded projection of
  the members that are not secret (section 9) instead of the whole object.
- `[Sensitive]` applies only to `LogObject` and the context of `*WithContext`. `Inspect`, `LogJson` and a
  message hole write the value as it is: do not pass a secret to them.
- An anonymous object cannot carry `[Sensitive]`. Do not put a secret in an anonymous context object.
- Do not log a raw request or response body or headers from a handler. The request diagnostics do that, with
  masking.

## 9. Guard argument assembly with `IsEnabled`

The extensions and the `Log*` calls check the level themselves, but only *after* their arguments are built.
When building the arguments costs something, guard the whole assembly, so nothing is built for an event that is
thrown away.

Guard when the arguments need: an anonymous object or projection (`new { … }`), a `string.Join`, a LINQ query, a
`ToList()`, serialization (`JsonSerializer.Serialize` for `LogJson`), or any computation done only for the log.

```csharp
if (log.IsEnabled(LogLevel.Trace))
    log.LogObject("Charge", new { fullAmount, previousAmount, creditAmount });

if (log.IsEnabled(LogLevel.Debug))
    log.LogDebug("State changes for DeviceUids: {DeviceUids}", string.Join(", ", changed));
```

- **Guard at the level of the call inside**: `LogLevel.Trace` for `LogObject`, `LogLevel.Debug` for `Inspect`,
  `LogJson`, `LogDebug`. A Debug guard around a `LogObject` still builds the object when Trace is off.
- Put every call that uses the assembled value inside the guard, and assemble nothing outside it.
- Do not guard a call whose arguments already exist: `LogObject(nameof(device), device)`,
  `Inspect(nameof(id), id)`, a constant message.
- A value the code needs anyway (the JSON it sends) is not assembly for the log: log it unguarded.
- Guards are for **Trace and Debug** only. Do not guard a Warning or Error, or its context object: those levels
  are on in production, and the event is rare.

## API reference

`using Microsoft.Extensions.Logging;` and `using Pondhawk.Logging;`. All are extension methods on `ILogger`.

| Call | Level | Notes |
|---|---|---|
| `MethodLogger EnterMethod([CallerMemberName] string method = "")` | Debug | Use with `using var`. The compiler fills in the calling method's name, in async methods too. `MethodLogger` is an `ILogger`. When Debug is off it returns a shared inert scope: no allocation. |
| `Inspect<T>(string name, T value)` | Debug | Writes `name = value`. |
| `LogObject<T>(string title, T value)` | Trace | JSON payload; honours `[Sensitive]`. `LogObject<T>(T value)` titles it with the type name. |
| `LogJson / LogSql / LogXml / LogYaml / LogText(string title, string? text)` | Debug | Text already formatted; the type is a display hint. |
| `ErrorWithContext(Exception cause, object context, string title)` | Error | Also `(object context, string title)` and `(Exception cause, string title)`. The title is not a template. |
| `WarningWithContext(…)` | Warning | Same three overloads. |
| `LogWithContext(LogLevel level, …)` | any | Same three shapes, with the level first. |

Every payload method (`LogObject`, `LogJson`, …) has an overload that takes a `LogLevel` first, to attach a
payload at another level when a failure needs it.

## Template

```csharp
public async Task<IResult> Handle(ThingRequest request, CancellationToken ct)
{
    using var log = logger.EnterMethod();

    log.LogObject(nameof(request), request);          // whole; secrets marked [Sensitive] on the type


    // *****************************************************************
    log.LogDebug("Attempting to fetch Thing");
    var thing = await db.Things.SingleOrDefaultAsync(t => t.Uid == request.ThingUid, ct);
    if (thing is null)
        return Results.NotFound();                    // expected failure: no log event

    log.LogObject(nameof(thing), thing);


    var (total, alreadyPaid, remaining) = Work.Amounts(thing, request);

    if (log.IsEnabled(LogLevel.Trace))                // the object is built only for the log
        log.LogObject("Amounts", new { total, alreadyPaid, remaining });

    if (remaining <= 0)
    {
        log.LogDebug("Covered by earlier payment - nothing to do");
        return Results.Ok();
    }


    // *****************************************************************
    log.LogDebug("Attempting to call the payment provider");
    ProviderReceipt receipt;
    try
    {
        receipt = await provider.Charge(thing, remaining, ct);
    }
    catch (ProviderException ex)                      // handled here, so logged here, once
    {
        log.ErrorWithContext(ex, new { request.ThingUid, remaining, ex.Code },
            "Payment provider refused the charge");
        return Results.Problem(statusCode: 502, title: "Payment provider unavailable");
    }

    log.Inspect("ReceiptId", receipt.Id);             // nameof(receipt.Id) would give only "Id"


    // *****************************************************************
    log.LogDebug("Attempting to save changes");
    thing.PaidAmount += remaining;
    await db.SaveChangesAsync(ct);                    // not handled here: not caught

    return Results.Ok(receipt.Id);
}
```

## Review checklist

- [ ] The method opens `using var log = logger.EnterMethod();` and logs through `log`.
- [ ] Each step has a banner and `LogDebug("Attempting to …")` before it runs, with no holes.
- [ ] Key inputs and step results are logged with `Inspect` (one scalar) or `LogObject` (objects), named with `nameof`.
- [ ] An object is logged whole, with `[Sensitive]` on its secrets — not as a row of `Inspect` calls on its properties.
- [ ] Several loose values are one guarded anonymous object, not several `Inspect` calls.
- [ ] A loop's step line is written once before the loop; each item is logged with `LogObject`.
- [ ] Each normal early exit has a Debug line that says why.
- [ ] Expected failures — including a dependency's exception that rejects the caller's input — are returned, never Warning or Error; a Debug "why" only when the status code does not explain itself.
- [ ] A transient dependency failure that ends the request is Warning; a wrong result or inconsistent data is Error.
- [ ] Where only this catch knows the consequence, it handles: `ErrorWithContext` with the repair ids, then returns the failure.
- [ ] When the consequence is true only after a later step, the Error is logged once, after that step.
- [ ] `catch (Exception)` only around one item's work or one pass, letting shutdown cancellation through.
- [ ] A background job opens its scope per pass; a failed item is Warning when it will be retried, Error when it is given up; each pass ends with Information (all ok), Warning (any failed or given up) or Debug (nothing to do); a pass that throws is Error in `ExecuteAsync`.
- [ ] Items skipped in a small loop are logged after it as one guarded object. A bulk loop (more than about 100 items, a file, an import) logs counts only.
- [ ] Step lines are short (a verb and a thing); in-memory work that decides the outcome is a step.
- [ ] A queue consumer: contention retry and stale message at Debug, dependency retry at Warning, last attempt and dead-letter at Error.
- [ ] A typed client that throws for its caller logs Debug only; it never `LogJson`s a raw body that may hold personal data.
- [ ] Entities in your own codebase carry `[Sensitive]` on personal data; projections only for types you cannot change.
- [ ] `[LoggerMessage]` is used only for fixed-template Information, Warning and Error events (or hot paths), never for step lines or payloads; its messages follow section 6, and no parameter is a secret.
- [ ] Each Warning and Error says what failed, which thing (named holes) and what happens now.
- [ ] No `"{Message}", ex.Message`. No catch that only logs and rethrows.
- [ ] Rich failure state goes in `ErrorWithContext` / `WarningWithContext`, not in a long message.
- [ ] Secrets carry `[Sensitive]` and never reach `Inspect`, `LogJson` or a message hole.
- [ ] No string interpolation in a log message.
- [ ] Every argument built only for the log (anonymous object, `string.Join`, LINQ, serialization) is inside an `IsEnabled` guard at the level of the call — `Trace` for `LogObject`.
- [ ] Information is used only for outcomes an operator needs, not for request steps.
