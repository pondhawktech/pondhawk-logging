# Pondhawk.Logging.CloudWatch

An Amazon **CloudWatch Logs** provider for [`Pondhawk.Logging`](../Pondhawk.Logging/README.md): a
ZLogger-based `Microsoft.Extensions.Logging` provider that writes each event as one JSON object, so Logs
Insights can filter on its fields without parsing text — from anywhere, with nothing installed on the host.

## Usage

```csharp
using Microsoft.Extensions.Logging;
using Pondhawk.Logging.CloudWatch;

builder.Logging.AddCloudWatch("/my-app/Production", "orders");
```

That writes Information and above to the stream `<instance-id>/orders` of the log group
`/my-app/Production`, using the SDK's default credentials and region. Pair it with
[`Pondhawk.Logging.Console`](../Pondhawk.Logging.Console/README.md) for a copy in the journal on the host.

```csharp
builder.Logging.AddCloudWatch("/my-app/Production", "orders", o =>
{
    o.StreamName = "ecs/orders/" + taskId;      // off EC2: name the stream yourself
    o.Credentials = roleCredentials;            // default: the SDK's chain (the instance role)
    o.Region = RegionEndpoint.USEast1;          // default: AWS_REGION / profile / instance metadata
    o.RetentionDays = 30;                       // for a group this provider creates
});
```

| Option | Default | |
|---|---|---|
| `StreamName` | `null` | Null names the stream `<instance-id>/<service>` from EC2 instance metadata. Set it to log from anywhere else. |
| `Credentials` | `null` | Null uses the SDK's default credential chain. |
| `Region` | `null` | Null lets the SDK find the region. |
| `RetentionDays` | `30` | Given to a group this provider creates; an existing group's retention is never touched. |
| `BatchSize` / `FlushInterval` | `500` / 1 s | A send happens at whichever comes first. Clamped to 1–10,000 and to at most an hour. |
| `MaxHeldEvents` | `100` | Warning-and-above events held while there is nowhere to send them. |

## The event

One JSON object per event, in Pascal case:

```json
{
  "Level": "Error",
  "Category": "MyApp.Orders.OrderService",
  "CorrelationId": "01K6...",
  "Title": "order failed",
  "Context": { "OrderId": 42, "Customer": "acme" },
  "Exceptions": [
    { "Type": "System.InvalidOperationException", "Message": "bad state",
      "StackTrace": [ "at MyApp.Orders.OrderService.Process(Int32 id) in ...", "at ..." ] },
    { "Type": "System.Data.DbException", "Message": "timeout", "StackTrace": [ "at ..." ] }
  ]
}
```

The shape follows what the event carries, never a guess at its text:

- **An exception** gives `Exceptions`: the whole chain, outermost first, each with its `Type`, `Message`
  and a `StackTrace` of one frame per array entry (so the console shows it line by line, not as one line
  of `\n`s).
- **`ErrorWithContext(cause, context, message)`** adds `Context`, the context object as nested JSON.
  `Context` is always an object — a context that serializes to a string, number or array is wrapped as
  `{ "Value": ... }` — so `Context.OrderId` can always be queried.
- **`LogJson` / `LogObject`** give `Payload` as nested JSON, never JSON inside a string. JSON that does not
  parse stays text rather than being lost.
- **`LogSql` / `LogXml` / `LogYaml` / `LogText`** give `Payload` as text, or an array of lines when there
  are several.
- `CorrelationId` is the ambient correlation (`CorrelationManager`), left out when there is none.
  `Nesting` is the `EnterMethod` delta (`1` / `-1`), present only on method-tracing events.

```
fields @timestamp, Title, Context.OrderId
| filter Level = "Error" and Category like /Orders/
| sort @timestamp desc
```

A metric filter for an error alarm is `{ $.Level = "Error" || $.Level = "Critical" }`.

Every event is kept under 256 KB, whatever it carries. The title (16,384 characters), the category, the
correlation id, each exception message (16,384 characters) and the exception chain (32) are cut to fixed
lengths. An event still too large loses its context first, then payload lines, and stack frames only as a
last resort — each cut is marked in the event. One that cannot be cut to fit that way is written as its
level, category and title alone, with `"Truncated": true`.

### What reaches CloudWatch

Everything logged at Information and above, as logged. `[Sensitive]` masks attributed members of the
objects the logging API serializes — `LogObject`, and the context passed to `ErrorWithContext`. It does
**not** cover message arguments (`LogInformation("... {Token}", token)`), strings passed to
`LogJson`/`LogText`/`LogSql`/`LogXml`/`LogYaml`, exception messages, or anonymous-type contexts, which
cannot carry the attribute. Keep secrets out of those.

## It cannot stop or slow the host

- **Adding it touches no network.** The AWS client is built, the stream name resolved and the stream
  created on the first batch, on the provider's own flush thread. A client that cannot be built — no region
  configured, for one — turns CloudWatch off; it does not throw into the host.
- **The stream and the group are created for you.** An existing stream is reused (a restarted service). A
  missing group is created and given `RetentionDays` — only when this host is the one that created it.
- **A verdict on the configuration turns it off, noted once on stdout.** No client, or no `StreamName` and
  no instance id after asking three times a minute apart (instance metadata can be out of reach while a
  host comes up): off until restart. Access denied on the group — creating the stream or putting events:
  off until a [rebind](#a-group-that-arrives-later) names another.
- **Anything else pauses it for a minute** — a timeout, a throttle, an outage. Each call is one attempt of
  at most five seconds with no SDK retries, so an outage costs the flush one bounded call a minute rather
  than one per batch.
- **Warnings and errors survive a pause.** The most recent `MaxHeldEvents` events at Warning and above are
  held and go out, in time order, as soon as they can: with the next batch, or on their own once the pause
  is over. They get one last attempt when the provider is disposed, so the error a service logs on its way
  down is not lost to a pause. Lower levels are discarded, unformatted.
- **No input can stop it.** A batch CloudWatch refuses as invalid is dropped and noted, never retried, so
  one bad event cannot hold up the rest. An event that cannot be formatted — an exception whose `Message`
  throws — is reported with what can be said of it. Option values that make no sense are clamped.
- **Rejected events are noted.** CloudWatch accepts a batch and silently rejects events more than two hours
  in the future or older than 14 days; that usually means the host's clock is wrong, and it is said on
  stdout, at most once a minute.

Notes go straight to stdout (prefixed `<4>` under journald), never through the logging factory, so a
failing provider cannot feed its own failure back into itself.

## A group that arrives later

`AddCloudWatch(logGroup, service)` fixes the group for the process. Where it is not known at startup — a
host told where to log by a plan that arrives later — pass a `CloudWatchDestination`:

```csharp
var destination = CloudWatchDestination.Unbound();
builder.Logging.AddCloudWatch(destination, "agent");

// later, when the plan arrives
destination.Rebind(CloudWatchDestination.Sanitize($"/my-app/{application}-{environment}"));
```

While unbound nothing is called, nothing is noted and no failure is counted; Warning and above is held,
so the events describing how the process came up reach CloudWatch once a group is named. `Rebind` returns
`false` and disturbs nothing when handed the group already in use, so it can be called on every plan. A
rebind to a different group tears nothing down: the stream is made in the new group on the next batch. The
destination is also registered as a singleton, so it can be resolved from DI.

`CloudWatchDestination.Sanitize` replaces every character a log group name may not contain with `-`.

## Level

Fixed: **Information, Warning, Error and Critical always go to CloudWatch; Debug and Trace never do.**
There is no setting and no switch. The floor is a filter scoped to the CloudWatch provider, so it does not
follow the Watch switch table — a provider-scoped rule is more specific than `AddWatch`'s global switch
filter — and it does not clamp the Watch provider registered beside it.

The floor applies to **every category**, the framework's included. Because the rule is scoped to the
provider, a host's usual `"Logging": { "LogLevel": { "Microsoft.AspNetCore": "Warning" } }` does not reach
CloudWatch, so ASP.NET Core request logs, `HttpClient` request URIs and EF Core command text at Information
all go there. To keep a category out, raise it for this provider:

```json
{ "Logging": { "CloudWatch": { "LogLevel": { "Microsoft": "Warning", "System.Net.Http": "Warning" } } } }
```

Raising works; lowering does not. Debug and Trace are refused by the provider itself, whatever a rule says.

Note that `ILogger.IsEnabled` is true when *any* provider would keep the event, so with CloudWatch
registered every Information call site formats, whatever the switch table says for its category.

## IAM

The least a host needs — create the group ahead of time, and scope the policy to that one group:

```json
{
  "Effect": "Allow",
  "Action": [ "logs:CreateLogStream", "logs:PutLogEvents" ],
  "Resource": [
    "arn:aws:logs:<region>:<account>:log-group:/my-app/Production",
    "arn:aws:logs:<region>:<account>:log-group:/my-app/Production:*"
  ]
}
```

A host that meets a missing group it may not create notes it once and turns CloudWatch off.

To have the provider create a missing group, add `logs:CreateLogGroup` and `logs:PutRetentionPolicy` — and
know what that grants. `PutRetentionPolicy` lets whoever holds the role shorten the retention of any group
the resource pattern covers, which deletes its older history; a wildcard such as `/my-app/*` extends that,
and the ability to write into other hosts' streams, to every group under the prefix. Keep the pattern as
narrow as the groups this host actually writes to. The stream name is the only thing that identifies the
writing host.
