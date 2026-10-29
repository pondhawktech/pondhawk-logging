# Pondhawk.Logging.Journald

A native **systemd-journald** sink for [`Pondhawk.Logging`](../Pondhawk.Logging/README.md): a ZLogger-based
`Microsoft.Extensions.Logging` provider that writes each event to the journal over its native protocol, with
the event's parts as journal fields (`CATEGORY`, `TITLE`, `CORRELATION_ID`, `CONTEXT_JSON`, `PAYLOAD_JSON`,
`EXCEPTIONS_JSON`, …), so a shipper such as `pondhawk-logship` can route by field and pass the event on whole.

**Status: in development** — see `docs/plans/2026-10-29-journald-sink.md` (issue #37). Not yet usable.
