# Pondhawk.Logging.Journald

A native **systemd-journald** sink for [`Pondhawk.Logging`](../Pondhawk.Logging/README.md): a ZLogger-based
`Microsoft.Extensions.Logging` provider that writes each event to the journal over its native protocol, with
the event's parts as journal fields. A shipper such as `pondhawk-logship` can route by `CATEGORY` and turn the
fields back into the JSON object [`Pondhawk.Logging.CloudWatch`](../Pondhawk.Logging.CloudWatch/README.md)
writes, with no AWS access on the host.

## Usage

```csharp
using Microsoft.Extensions.Logging;
using Pondhawk.Logging.Journald;

builder.Logging.ClearProviders();
builder.Logging.AddJournald();
```

That writes Information and above. Set another floor, or the identifier the events carry:

```csharp
builder.Logging.AddJournald(LogLevel.Debug, o => o.SyslogIdentifier = "pondhawk-gateway");
```

| Option | Default | |
|---|---|---|
| `SyslogIdentifier` | the executable's name | The `SYSLOG_IDENTIFIER` on every event. The default is what journald gives a service's stdout lines when its unit sets no `SyslogIdentifier=`; set it to match a unit that does. |

Pair it with [`Pondhawk.Logging.Watch`](../Pondhawk.Logging.Watch/README.md) for the switch-driven developer
view. Use it in place of [`Pondhawk.Logging.Console`](../Pondhawk.Logging.Console/README.md), not beside it:
both write to the journal.

## The fields

One journal entry per event. No field has journald's leading `_`, and a field with nothing to say is left out.

| Field | |
|---|---|
| `MESSAGE` | What `journalctl` shows: `<Category>: <Title>`, then ` \| <Type>: <Message>` of the outermost exception. |
| `PRIORITY` | The syslog priority: Critical `2`, Error `3`, Warning `4`, Information `6`, Debug and Trace `7`. |
| `SYSLOG_IDENTIFIER` | `JournaldOptions.SyslogIdentifier`. |
| `CATEGORY` | The logger category. |
| `TITLE` | The formatted message. |
| `CORRELATION_ID`, `SUBJECT`, `TENANT` | From the ambient `IOperationContext` (Pondhawk.Context). |
| `NESTING` | The `EnterMethod` delta (`1` / `-1`), on method-tracing events only. |
| `CONTEXT_JSON` | The `…WithContext` context as JSON, always an object (`{"Value":…}` otherwise). |
| `EXCEPTIONS_JSON` | The exception chain, outermost first: `Type`, `Message` and a `StackTrace` of one frame per array entry. |
| `PAYLOAD_JSON` | A `LogJson` / `LogObject` payload, as JSON. |
| `PAYLOAD` | A text payload (`LogSql`, `LogXml`, `LogYaml`, `LogText`): its lines joined by `\n`. |
| `TRUNCATED` | `1` when anything in the event was capped or cut. |

At most one of `EXCEPTIONS_JSON`, `PAYLOAD_JSON` and `PAYLOAD` is present: an event with an exception drops
its payload, as CloudWatch does.

```
$ journalctl -t orders -o verbose
    PRIORITY=3
    SYSLOG_IDENTIFIER=orders
    MESSAGE=Orders.OrderService: order failed | System.InvalidOperationException: bad state
    CATEGORY=Orders.OrderService
    TITLE=order failed
    CORRELATION_ID=01K6...
    CONTEXT_JSON={"OrderId":42,"Customer":"acme"}
    EXCEPTIONS_JSON=[{"Type":"System.InvalidOperationException","Message":"bad state","StackTrace":["at ..."]}]
```

**The same content as CloudWatch.** Both packages compile the same event shaping (`src/Shared/Events`), so
the fields hold what CloudWatch's JSON object holds: `Level` from `PRIORITY`, `Context` from `CONTEXT_JSON`,
`Exceptions` from `EXCEPTIONS_JSON`, `Payload` from `PAYLOAD_JSON`, or from `PAYLOAD` (a string for one line,
an array for several). The caps match too: title 16,384 characters, exception messages 16,384, chain 32,
and the event 256 KiB, cut first in context, then payload lines, then stack frames. A test rebuilds the JSON
from the fields and compares it with CloudWatch's.

`journalctl -o json` writes `null` for a value over 4,096 bytes; pass `--all` to read it.

### What reaches the journal

Everything at the floor and above, as logged. `[Sensitive]` masks attributed members of what the logging API
serializes — `LogObject`, and the context of a `…WithContext` call. It does **not** cover message arguments,
strings given to `LogJson`/`LogText`/`LogSql`/`LogXml`/`LogYaml`, exception messages, or anonymous-type
contexts. Keep secrets out of those, or hold the category back with a floor (see [Level](#level)).

## Sending

- **Synchronous.** Each event is formatted and sent inside the logging call, on the calling thread: one
  datagram to `/run/systemd/journal/socket`. Nothing is queued, so a command-line tool that logs and exits has
  every line in the journal, and disposing has nothing to flush.
- **Large events** — over one datagram — go as journald's own client does: written to a sealed `memfd` and
  passed with the socket message. They arrive whole.
- **A send journald does not take** — journald restarting, its buffer full — writes the event's console line
  to stdout instead. Under systemd stdout is the journal too, so the event is kept, as text.
- **Nothing throws into the caller**, whatever the event holds.

## No journal: the console

At start the provider decides, once, where events go. They go to the journal's socket only when both hold:

- the socket exists, and
- `JOURNAL_STREAM` names this process's stdout or stderr — systemd started the process with its output
  connected to the journal.

Otherwise each event goes to stdout as the line `AddJournaldConsole` writes:

```
<6>Orders.OrderService: order placed
<3>Orders.OrderService: order failed | System.InvalidOperationException: bad state | at ...
```

So a developer's terminal, a test run, a tool run over SSM Run Command, and a child process whose output was
redirected all get readable lines. Windows and macOS always get the console. The journal path needs 64-bit
Linux.

## Level

Information by default, or the floor given to `AddJournald`. The floor is a rule scoped to this provider,
under the alias `Journald`, so configuration can change it per category:

```json
{ "Logging": { "Journald": { "LogLevel": { "Default": "Warning", "Orders": "Debug", "Microsoft": "Warning" } } } }
```

or, in a systemd `EnvironmentFile`, `Logging__Journald__LogLevel__Microsoft=Warning`. `Default` there replaces
the floor; a category can raise or lower it.

**The host's provider-less category rules apply too.** In standard MEL a rule scoped to a provider hides the
provider-less rules from it, so a host's

```csharp
builder.Logging.AddFilter("Gateway.Credentials", LogLevel.Warning);
```

or `"Logging": { "LogLevel": { "Gateway.Credentials": "Warning" } }` would not reach the journal. This
provider applies them anyway, as an extra floor: an event must pass both. That is not standard MEL; it keeps a
floor written the usual way — to keep credentials out of the journal, say — from silently not applying.

- Only rules that name a category count: not `Default`, not `SetMinimumLevel`, not filter delegates (so not
  Watch's switch filter). The most specific category wins, matched as MEL matches it (dotted prefix, one
  `*`, ignoring case).
- A category under `Journald` cannot go below a provider-less floor for it.
- Configuration reloads apply.

**Watch beside it.** A Watch switch neither raises nor lowers the journal, and the journal's floor never holds
a switch down. `ILogger.IsEnabled` is true when any provider would keep the event, so with the journal at
Information every Information call site formats, whatever the switch table says.

**Debug and Trace** both arrive as `PRIORITY=7`; the journal has no level below it.
