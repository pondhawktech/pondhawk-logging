# Answers to the 2.1 response

From the Pondhawk One team. Four questions you left open, plus one new one.

---

## 1. The rebind behaviour change — accepted

**Queued events going to the new destination, carrying the new domain, is what we want.**

You were right that it is ours to accept, and it costs us nothing, for a reason
worth stating: our old behaviour never delivered those events anywhere. A
packaged host ships with `WatchDomainName: ""` and `WatchEventStoreUri: ""` —
blank on purpose — so the agent's baked configuration builds a quiet factory
with no sink at all. There were no in-flight events at the moment of the swap
because nothing was buffering them.

Going forward your semantics are strictly better for us. The events emitted
between process start and the first mission plan are exactly the ones that
describe how the agent came up, and they now stand a chance of landing where
someone is looking.

**We accept the associated loss, having read the processor.** While the circuit
is open, only Warning and above is buffered, capped at 1000, and everything
below is counted into `DroppedEventCount`. So pre-plan Debug and Info tracing
will not survive to be flushed. That is the right trade — startup tracing is not
worth holding memory for — but see question 5, because it is the same mechanism.

## 2. Typed payloads defaulting to Debug — confirmed

Right call, and it needs nothing from us. It restores exactly what we have
today: `Fabrica.Watch` emitted `LogJson`, `LogYaml`, `LogSql` and `LogXml` at
Debug and only `LogObject` at Trace. Our 44 `LogObject`/`LogJson`/`LogYaml` call
sites keep the levels they already have, so the migration is a rename rather
than a behaviour change for any of them.

Your sharper framing of the original problem is the one we will quote
internally: the gate is the per-category switch, and `DefaultLevel` is Warning,
so a Trace payload was dropped on every category nobody had explicitly switched
up — which is where an unexpected error lands by definition.

## 3. `WarningWithContext` — no, thank you

Not now. We have no callers, and we have just spent several days deleting 7,300
lines of vendored code that nothing called; adding API on the same day on the
grounds that we might want it later would be poor form. Easy to ask for if a
call site ever appears.

## 4. The filter-scoping question — confirmed, no problem

You asked us to confirm this against our startup path rather than discover it in
a packaged host. We tested it directly instead of reasoning about MEL's rule
selection.

**Method.** A console app on `Pondhawk.Logging.Watch` 2.1.11 and
`Pondhawk.Logging.Console` 2.0.11, registering both providers, with the Watch
switch table deliberately set to disagree with the console:

```csharp
var destination = new WatchDestination("http://127.0.0.1:59999", "FilterTest");
using var factory = LoggerFactory.Create(b =>
{
    b.AddWatch(destination, o => o.DefaultLevel = LogLevel.Debug);  // global filter
    b.AddJournaldConsole();                                          // provider-scoped, Warning
});
```

Watch points at a dead port; only filtering is under test.

**Result.** The console emitted only:

```
<4>Pondhawk.One.FilterProbe: WARNING-expected
<3>Pondhawk.One.FilterProbe: ERROR-expected
```

Trace, Debug and Information did not reach the console. The provider-scoped rule
wins for the console provider; `AddWatch`'s global filter governs Watch alone and
does not flood it. **We reversed the registration order and got the same result**,
so it does not depend on which is added first.

**One observation to pass back.** The composite `IsEnabled(LogLevel.Debug)`
returns `true` in that configuration, which is correct — Watch wants Debug — but
it means a Debug call site still formats its message even in a process where only
the console is listening. Your `SetMinimumLevel(Trace)` plus global filter gives
switch-dropped *categories* a zero-work short-circuit, as your comment says; it
does not give one to levels that only the console would have discarded. Fine for
us, and probably worth a sentence in the README so nobody expects otherwise.

---

## 5. A new question: a destination that is not yet bound

This is the one thing in 2.1 we cannot make work, and it is the same scenario
that motivated item 3.

**The problem.** `WatchDestination(string serverUrl, string domain)` guards both
arguments non-empty. The domain-only constructor that would accept a relative
binding is `internal`. Our packaged hosts have no destination at startup — that
is the entire point of the design:

```yaml
# packaging/config/agent.yml, shipped in the RPM
RealtimeLogging: false
WatchDomainName: ""
WatchEventStoreUri: ""
```

Blank is baked deliberately, so one AMI can run any mission. The orchestrator
learns its destination from EC2 user-data at launch; the agent learns it from
the mission plan, which may arrive minutes later or never.

So at startup we cannot construct a `WatchDestination`, therefore cannot call
`AddWatch`, therefore have nothing registered to `Rebind` when the plan finally
names a destination. The capability lands exactly one step out of reach.

**The workaround, and why we would rather not.** We can construct with a
sentinel URL and rebind on the first plan. It works, but it means every packaged
agent spends its early life posting to a server that does not exist: three
failures, circuit opens, and everything below Warning is dropped until the plan
arrives. We would be manufacturing an outage to model "not configured yet".

**What we would like.** A destination that is explicitly unbound until told
otherwise — shape entirely yours, but something like:

```csharp
var destination = WatchDestination.Unbound("agent");   // or Unbound()
builder.Logging.AddWatch(destination);
// ... later
destination.Rebind(plan.WatchEventStoreUri, plan.WatchDomainName);
```

with the processor treating unbound as "discard quietly, do not count failures,
do not open the circuit" rather than as an unreachable server. Whether unbound
events are dropped or held is your call — dropping is fine for us, and is what
happens today.

**If you would rather not,** say so and we will use the sentinel; we would just
rather not have a permanently open circuit breaker and a climbing
`DroppedEventCount` be the normal state of a healthy agent, because that is the
sort of thing that teaches people to ignore the metric.

---

## Where that leaves us

Items 1–4 are accepted as shipped, and item 5 stays declined with the churn
ours. Question 5 above is the only thing between us and starting the migration,
and it has a workaround if you would rather not take it.
