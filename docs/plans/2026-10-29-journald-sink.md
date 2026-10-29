# Plan: Pondhawk.Logging.Journald — a native journald sink

Status: **DECIDED** — 2026-10-29. From issue #37 (Pondhawk One team). Decided 2026-10-29: the provider applies
provider-less category rules (§5, A); the formatter is shared by source (§1); the live test runs in CI (§6).

## Why

Pondhawk One's orchestrator, gateway and image tool log to the journal, and `pondhawk-logship` ships the journal
to CloudWatch Logs. `Pondhawk.Logging.Console` flattens each event into one text line, so `Context`, `Payload`,
`CorrelationId` and the exception chain reach CloudWatch as text, and logship cannot route by category. The
gateway has no AWS access, so `Pondhawk.Logging.CloudWatch` is not an option there.

## Package

- `src/Pondhawk.Logging.Journald`, namespace `Pondhawk.Logging.Journald`, `net8.0`, version `1.0`.
- References: ZLogger, `Microsoft.Extensions.Logging.Abstractions`, `Microsoft.Extensions.Configuration` binding
  only as MEL already brings it, `Pondhawk.Context`. **Not** Pondhawk.Logging (the `CheckReferences` rule), **not**
  Pondhawk.Logging.CloudWatch (it brings the AWS SDK).
- Own `LogStateContract` copy, checked by a `ContractTests` in `test/Pondhawk.Logging.Journald.Tests`.
- Entry point: `AddJournald(this ILoggingBuilder, LogLevel floor = LogLevel.Information, Action<JournaldOptions>? configure = null)`.
  `JournaldOptions.SyslogIdentifier` (default: the process name, as journald derives it for stdout).
- `[Sensitive]` needs nothing here: the API masks when it serializes `LogObject` and the context
  (`LoggingJsonTypeInfoResolver`), before the provider sees the JSON.

## 1. One formatter for CloudWatch and the journal

The issue asks for the same content as CloudWatch's event, from a shared formatter.

**Proposal: shared source, not a shared package.** Move the event model and the cutting logic to
`src/Shared/Events/` (`LogEventParts`, `EventFormatter`), compiled into both packages as `internal` with
`<Compile Include="..\Shared\Events\*.cs" LinkBase="Shared" />`. No new package, no public surface, and the
"no package references the API" rule is untouched.

The formatter is split in two:

1. **Parts** — from a captured event: level, category, title, correlation id, subject, tenant, nesting,
   `Context` (always an object), `Payload` (JSON element, or lines), `Exceptions` (the chain, frames as
   arrays), `Truncated`. The field caps and the cut order (context, then payload lines, then stack frames,
   then minimal) stay as they are, bounded and non-throwing.
2. **Writers** — CloudWatch writes the parts as one JSON object (output byte-identical to today; the existing
   CloudWatch tests are the check). Journald writes each part as a journal field.

The size budget is a parameter: CloudWatch counts the JSON message, Journald counts the sum of field values.
Both are 256 KiB.

## 2. Fields

As in the issue's table, a fixed list, nothing with a leading `_`:

`MESSAGE`, `PRIORITY`, `SYSLOG_IDENTIFIER`, `CATEGORY`, `TITLE`, `CORRELATION_ID`, `SUBJECT`, `TENANT`,
`NESTING`, `CONTEXT_JSON`, `PAYLOAD_JSON`, `PAYLOAD`, `EXCEPTIONS_JSON`, `TRUNCATED`.

- `MESSAGE` = `<Category>: <Title>`, plus ` | <Type>: <Message>` of the outermost exception.
- `PRIORITY` uses the Console package's mapping (2/3/4/6/7/7).
- Optional fields are left out when empty.
- Payload with an exception: CloudWatch drops the payload when the event has an exception. Journald does the
  same, so the two stay identical.
- Wire format: `NAME=value\n` when the value has no newline; otherwise `NAME\n` + 64-bit little-endian length
  + value + `\n` (binary-safe).

## 3. Transport

- **Synchronous send in `Post`**, on the calling thread: one `sendto` on a `SOCK_DGRAM` Unix socket to
  `/run/systemd/journal/socket`. Nothing is queued, so nothing is lost at exit and dispose has nothing to flush.
  The README says so.
- **Large events**: on `EMSGSIZE` (or when the datagram is over the send buffer), do what `sd_journal_send`
  does: `memfd_create`, write, seal (`F_ADD_SEALS`: shrink, grow, write, seal), and `sendmsg` with the fd in
  `SCM_RIGHTS` and an empty body. P/Invoke to libc via `LibraryImport` (needs `AllowUnsafeBlocks`). Linux only;
  the console path covers every other OS.
- **A failed send** (journald restarting, `EAGAIN`, `ENOENT`) writes the console line for that event to
  stdout. Under systemd stdout is the journal too, so the text is kept. Never throws to the caller.

## 4. No journal: console fallback

Decided once, at provider construction:

- the socket exists, **and**
- `JOURNAL_STREAM` (`<dev>:<inode>`) names this process's stdout or stderr (`fstat` of fd 1 and fd 2).

Otherwise every event goes to the console as the line `AddJournaldConsole` writes (`<N>Category: Title | …`).
Share that line's formatting by source with `Pondhawk.Logging.Console` (same `src/Shared` approach), so the
two cannot drift.

## 5. Levels and configuration

- `JournaldLoggerProvider` is its own ZLogger provider type, `[ProviderAlias("Journald")]` (no dots), so
  `Logging:Journald:LogLevel:Microsoft` and `Logging__Journald__LogLevel__Microsoft` work through MEL's own
  configuration binding.
- The floor is a filter scoped to that provider, as `AddJournaldConsole` does. A provider-scoped rule always
  outranks provider-less rules in MEL's rule selection, so Watch's switch filter neither raises nor lowers the
  journal, and the journal's floor never holds a switch down.
- `Post` also refuses anything below the floor, as CloudWatch does, so a stray rule cannot open Debug.

**Decided (A) — the gateway's `LoggerFilterOptions` floors.** The same MEL rule cuts the other way: once a
provider-scoped rule exists, provider-less category rules (`options.AddFilter("Some.Category", Warning)`) **do not
apply to this provider**. Two ways to meet the issue:

- **A (chosen):** the provider also applies provider-less, category-specific rules (not `Default`, not
  filter delegates — so Watch's switch filter and `SetMinimumLevel` are excluded) as an extra floor: the event
  must pass both. Read from `IOptionsMonitor<LoggerFilterOptions>`, so reloads apply. Floors that keep
  credentials out of the journal then work without the gateway naming the provider. Cost: not standard MEL
  semantics; documented in the README.
- **B:** standard MEL only. The gateway scopes its floors to the provider (`AddFilter<JournaldLoggerProvider>(…)`
  or `"Journald"` rules). Simpler, but a floor written the usual way silently does not apply.

## 6. Tests

`test/Pondhawk.Logging.Journald.Tests`, against a fake socket (a bound `SOCK_DGRAM` in a temp directory, with
fd passing read back) and a fake `JOURNAL_STREAM`:

- Each field, per kind of event: plain, exception chain, context (object and scalar), each payload type,
  correlation / subject / tenant, method tracing.
- Newline value (length-prefixed form); binary-unsafe value; event over one datagram (memfd path); event over
  256 KiB (cut, `TRUNCATED=1`).
- Parity: for each event kind, the fields turned back into JSON equal what `Pondhawk.Logging.CloudWatch` writes.
- Levels: floor; per-category override from configuration and from an environment variable; a
  `LoggerFilterOptions` floor (§5); Watch alongside with a switch raised and lowered.
- Fallback: no socket; `JOURNAL_STREAM` unset; `JOURNAL_STREAM` not matching stdout.
- Failing send goes to stdout.
- **Live (`Category=Journald`)**, skipped unless journald is reachable: send events, read back with
  `journalctl -o json --identifier=<unique>`, compare every field. Runs in `build.yml` on `ubuntu-latest`, which has journald.

## 7. Also

- `CheckReferences`, `ContractTests`, solution file, `build.yml` pack list, package README.
- CLAUDE.md: a section for the package, "Six packages" in Architecture, the build command.
- No change to `Pondhawk.Logging.Console` behaviour; CloudWatch output byte-identical after the formatter move.
