# Pondhawk.Logging.Console

A ZLogger-based `Microsoft.Extensions.Logging` console optimized for **systemd-journald**, for Linux
production services. Each line is written for journald to parse, and the console is **fixed at Warning**
so only warnings, errors, and critical events surface.

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

Fixed at Warning — nothing below Warning reaches this console. Pair it with
[`Pondhawk.Logging.Watch`](../Pondhawk.Logging.Watch/README.md) for the rich, switch-driven developer view.
