# Pondhawk.Logging.Console

A ZLogger-based `Microsoft.Extensions.Logging` console optimized for **systemd-journald**, for Linux
production services. Each line is written for journald to parse, and the console is **at Warning by default**
so only warnings, errors, and critical events surface — or from a floor you choose.

## What "journald-optimized" means

- **sd-daemon priority prefix** — each line starts with `<N>` (the syslog priority mapped from the log
  level), so journald sets the entry's `PRIORITY` and `journalctl -p err` filtering works.
- **No timestamp** — journald stamps the time itself.
- **No ANSI color** — the journal stores raw text.
- **Category inline** — the logger category is included in the message (journald's identifier is
  process-level).
- **Single line per event** — exceptions render inline, so an error is one journal entry at the right
  priority rather than continuation lines that lose it.

Level → priority:

| LogLevel | prefix |
|---|---|
| Critical | `<2>` |
| Error | `<3>` |
| Warning | `<4>` |
| Information | `<6>` |
| Debug / Trace | `<7>` |

## Usage

```csharp
using Microsoft.Extensions.Logging;
using Pondhawk.Logging.Console;

builder.Logging.ClearProviders();
builder.Logging.AddJournaldConsole();
```

Output captured by journald:

```
<4>OrderService: disk space low
<3>OrderService: failed to load order 42 | System.InvalidOperationException: not found | at ...
```

At Warning by default — nothing below Warning reaches this console. A service whose journal is its record of
routine work, or a command-line tool whose output is a CI job's log, can set its own floor:

```csharp
builder.Logging.AddJournaldConsole(LogLevel.Information);
```

The floor is this console's alone; it does not change what any other provider keeps. Pair it with
[`Pondhawk.Logging.Watch`](../Pondhawk.Logging.Watch/README.md) for the rich, switch-driven developer view.
