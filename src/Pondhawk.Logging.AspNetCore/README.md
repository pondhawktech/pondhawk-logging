# Pondhawk.Logging.AspNetCore

ASP.NET Core request diagnostics for [`Pondhawk.Logging`](../Pondhawk.Logging/README.md): three middlewares
that put a **correlation id, subject and tenant** on every event of a request, **bracket each request** with
its status and elapsed time, and — when switched on — **dump the whole request**.

They log through `Microsoft.Extensions.Logging` under one category, `Pondhawk.Diagnostics.Http`, so they work
with any provider. The package depends on `Pondhawk.Logging` and the ASP.NET Core shared framework, and on no
sink.

## Usage

```csharp
using Pondhawk.Logging.AspNetCore;

builder.Services.AddPondhawkDiagnostics(options =>
{
    options.Tenant = http => http.Request.RouteValues["tenant"]?.ToString();
    options.Skip = http => http.Request.Path.StartsWithSegments("/health");
    options.MaskedBodyFields.Add("password");
});

app.UseDiagnosticsMonitor();      // early: every request, including those refused later
app.UseExceptionHandler("/error");
app.UseRouting();
app.UseAuthentication();
app.UseAuthorization();
app.UseDiagnosticsEnrichment();   // after authentication: subject and tenant
app.UseRequestLogging();          // deep: the opt-in dump
```

`AddPondhawkDiagnostics` is optional; without it the middlewares run on the defaults. Each middleware stands
on its own — use one, two or all three.

## The three middlewares

| Call | Where | What it does |
|---|---|---|
| `UseDiagnosticsMonitor` | Early — first, or right after forwarded headers | Settles the correlation id, then logs `Begin GET /orders/7` and `End GET /orders/7 -> 200 (12.4ms)` at Debug — with the exception, when the request failed. |
| `UseDiagnosticsEnrichment` | After authentication | Sets the subject (from the principal) and the tenant (from your delegate) for everything logged within the request. |
| `UseRequestLogging` | Deep — after routing and authentication | When the category is Debug-enabled, logs the whole request as a JSON payload — with the endpoint and user it resolved to — then the response the same way. |

**Why the monitor and the request logger are two things.** They differ by position, and neither position does
both jobs. The monitor is the outer bracket: registered first, it times the whole pipeline and still closes a
request that authentication refuses or that throws — but that early there is no route and no user. The request
logger sits deep, where the request is fully resolved, and accepts that anything short-circuited before it
never arrives. The monitor logs the path only, never the query string.

A request that fails carries its exception on the End line, so the failure and its timing are one event. That
holds whether the exception reached the monitor — it is then closed as a 500, since that is what the client is
sent — or an exception handler inside the bracket caught it first and answered.

### Correlation id

Taken from the inbound `X-Correlation-Id` when it is well formed, otherwise the ambient one, otherwise a fresh
one; set as the ambient `IOperationContext.CorrelationId` (Pondhawk.Context) and echoed on the response so a caller can quote it. An id from outside
is accepted only if it is 1–64 ASCII letters, digits, `-`, `_` or `.` — a public server can be sent anything,
and the value lands on every event of the request. Anything else is ignored and replaced.

Whichever of the monitor and enrichment runs first settles it, so with the monitor in place a request refused
before authentication has a correlation id too.

**Use the same id in your own responses and records.** Once it is settled, everything later in the request —
your middleware, endpoints, exception handler, an audit journal — reads it from the ambient context, so an
error body or an audit row carries the id the caller was sent and every event of the request is stamped with:

```csharp
using Pondhawk.Context;

var correlationId = Ambient<IOperationContext>.Current.CorrelationId;
```

**Where the exception handler goes.** Register it **after** the monitor, as in the usage above. The monitor then
closes the request with the status the handler chose (a 400 for a validation error, say) and the exception on
its End line. Registered before the monitor it still reads the same correlation id, but the monitor has
already closed the request by the time the handler answers, so its End line says 500 whatever the handler
then sends.

### Subject and tenant

The subject on each event defaults to the `preferred_username` claim, so it reads as a person rather than an id;
without one it is the `client_id` claim, and only as a last resort the `sub` claim, then the name identifier claim.
`options.Subject` replaces that. That is the subject's name, `IOperationContext.SubjectName`; its id,
`SubjectId`, is the `sub` claim (or the name identifier), or the `client_id` for a machine acting for itself. A
request with no caller has `anonymous` as both, so a log or an audit trail never shows a blank — code that
learns later who is acting, as a sign-in page does, sets them itself. The tenant comes from `options.Tenant` and
is not set without it — for a tenant carried in the caller's token:

```csharp
builder.Services.AddPondhawkDiagnostics(options =>
    options.Tenant = http => http.User.FindFirst("tenant")?.Value);
```

All of it is the ambient `IOperationContext` (Pondhawk.Context): it stays in the process,
is stamped on each event by the sink (Watch, CloudWatch), and is there for anything else that reads it.

All three values attach to the request's `Activity`. ASP.NET Core starts one whenever its own logging or a
diagnostic listener is enabled; without one there is nothing to attach them to, and the request goes on
without them.

## What the request dump shows

**The whole request, then the response.** This is a diagnostics tool, and hidden data defeats it. The dump is
gated twice — the application must call `UseRequestLogging`, and someone must switch
`Pondhawk.Diagnostics.Http` to Debug — and when it is on it logs two events, each with a JSON payload
(`LogJson`).

JSON rather than text because a viewer can do more with it: Watch indents it, colours it and folds it, so the
headers collapse out of the way and a JSON body reads as JSON instead of one long line.

### The request — `HTTP POST /orders/7/lines`

```json
{
  "Method": "POST",
  "Url": "/orders/7/lines?returnUrl=%2Fcart&tag=a&tag=b",
  "Query": { "returnUrl": "/cart", "tag": ["a", "b"] },
  "Endpoint": {
    "Name": "HTTP: POST /orders/{id:int}/lines",
    "Route": "/orders/{id:int}/lines",
    "Values": { "id": "7" }
  },
  "User": { "Name": "pat", "Subject": "user-42", "Scheme": "Bearer" },
  "Headers": {
    "Content-Type": "application/json",
    "Authorization": "Bearer ***(11)",
    "Cookie": "sid=***(32); theme=***(4)"
  },
  "Body": { "sku": "A1", "qty": 2 }
}
```

- **`Url`** — the path and query string exactly as sent, so an encoding problem shows.
- **`Query`** — the same query decoded, one property per parameter; a name sent more than once is an array.
  Left out when there is no query string.
- **`Endpoint`** — where routing sent the request: the endpoint's name, its route pattern and the route
  values. The pattern is the thing to group by; the path is not. Left out when no endpoint matched.
- **`User`** — who authentication made of the caller: name, subject (from `options.Subject`) and
  authentication scheme. Left out for an anonymous request.
- **`Headers`** — in full, except the masked ones (below).
- **`Body`** — what was sent, up to `MaxBodyBytes`: a JSON body as nested JSON, a form post as an object of its
  decoded fields, any other text (XML, `text/*`) as a string. The body is buffered and rewound, so the endpoint
  reads it from the start. A form that something earlier in the pipeline already parsed (an authentication
  handler serving a token endpoint) is written from the parsed fields, since its stream may be spent.

`Endpoint` and `User` are why this middleware is registered deep: before routing and authentication there is
neither.

### The response — `HTTP POST /orders/7/lines -> 201`

```json
{
  "Status": 201,
  "Elapsed": 12.4,
  "Headers": {
    "Content-Type": "application/json; charset=utf-8",
    "Location": "/orders/7/lines/3",
    "Set-Cookie": "sid=***(32); Path=/; HttpOnly; SameSite=Lax"
  },
  "Body": { "id": 3, "sku": "A1", "qty": 2 }
}
```

Status, elapsed time (from this middleware, in milliseconds) and headers are always there. **The body is not,
unless you ask**: `options.CaptureResponseBody = true` puts a stream in front of the response that copies the
first `MaxBodyBytes` of a text body as it is written. Everything still goes straight to the client — nothing is
held back, so streaming behaves as it did — but it is one more thing in the response's path for every dumped
request, which is why it is off by default.

A request that throws has no response event; the exception is on the monitor's End line.

### Beside the body

What is said *about* a body, request or response, goes beside it, never inside it:

| Property | When | Holds |
|---|---|---|
| `BodyOmitted` | The body was not written. | `ContentType`, `Length`, and `Reason`: `not text` (multipart, binary), `content-encoded` (compressed), `capture is off`, or `not valid JSON, so the masked fields could not be found` |
| `BodyCut` | The body ran past `MaxBodyBytes`. `Body` is then the text that was kept, as a string. For a form already parsed, `Body` holds the whole fields that fit, counting each name and value as UTF-8. | `Written`, `Length` |
| `BodyError` | The request body could not be read. | The error message |

### Masking

**By name, and yours to choose.** Three headers are masked by default; nothing else is hidden unless you name
it:

```csharp
options.MaskedBodyFields.Add("password");        // form fields and JSON properties, request and response
options.MaskedQueryParameters.Add("code");       // query parameters
options.MaskedHeaders.Add("X-Api-Key");          // headers — or .Clear() to show them all
```

- **Headers** — `Authorization`, `Cookie` and `Set-Cookie` by default. They are replayable credentials, and
  what says something without giving the secret away is kept: the authorization scheme, the cookie names, and
  a `Set-Cookie`'s attributes (`Path`, `Expires`, `SameSite`). The set covers request and response headers.
- **Query parameters** — masked in both `Url` and `Query`.
- **Body fields** — a form post's fields, and a JSON body's properties **at any depth**, in the request and in
  a captured response. A masked JSON string becomes its length; a masked number, object or array becomes
  `***`.

A masked value is replaced by its length (`***(7)`), so an empty or truncated one still shows. A header or
query parameter gives its length as sent; a form field gives its decoded length, so `abc%20def` is `***(7)`
whether or not the form was parsed before the dump. Names match without regard to case.

Two limits worth knowing:

- **A JSON body that cannot be parsed is left out** while `MaskedBodyFields` names anything — malformed, or
  cut short by `MaxBodyBytes`. The fields could not be found in it, and the application asked for them not to
  be logged. With nothing named, it is written as the text it is.
- **XML and plain text are not searched.** A secret in one of those is written as sent.

If an application takes passwords, client secrets or tokens, name those fields — with the defaults, a sign-in
is logged with its password whenever the category is at Debug.

When the category is not Debug-enabled the request logger does nothing at all: no body is read, buffered or
copied.

## Options

| Option | Default | Purpose |
|---|---|---|
| `CorrelationHeader` | `X-Correlation-Id` | The inbound and echoed header. |
| `EchoCorrelationId` | `true` | Write the id on the response. |
| `Subject` | `sub`, then name identifier (on each event: `preferred_username`, then `client_id`, first) | `Func<HttpContext, string?>`. |
| `Tenant` | none | `Func<HttpContext, string?>`. |
| `Skip` | none | `Func<HttpContext, bool>`; true leaves a request out of the monitor and the dump. It still gets its correlation id, subject and tenant. |
| `MaskedHeaders` | `Authorization`, `Cookie`, `Set-Cookie` | Request and response headers masked in the dump. Add to it or clear it. |
| `MaskedQueryParameters` | empty | Query parameters masked in the dump. |
| `MaskedBodyFields` | empty | Form fields and JSON properties (any depth) masked in the dump, request and response. |
| `CaptureBody` | `true` | False describes every request body without reading it. |
| `CaptureResponseBody` | `false` | True keeps a copy of a text response body for the dump. |
| `MaxBodyBytes` | 65536 | How much of a body, request or response, is written; at most 1 MB. |
| `EndLevelByOutcome` | `false` | Log the monitor's End line at Information, Warning for 401/403/429 and Error for 5xx, instead of Debug. |
| `Probe` | `true` | Honour a diagnostic probe token. Only ever accepted when a provider supplies the key (the Watch provider does). |
| `ProbeHeader` | `X-Diagnostics-Probe` | The header a probe token is read from. |

## The diagnostic probe

Support can turn logging up for **one user's requests** in production without touching the switch table.
Support issues a **probe token** for a domain in Watch Server — bound to a user (up to 4 hours) or unbound,
for a user who cannot sign in (up to 1 hour) — and the user pastes it into the client's support setting. The
client sends it on every request in `X-Diagnostics-Probe`, the header Fabrica clients already send. A probed
request logs at **Trace whatever the switches say**, request dump included, and its events are tagged
`Diagnostics`.

The token is signed by Watch with a key only Watch holds; the application checks it against the domain's
public key, which the Watch provider receives with its switches. So the end user needs no role, the
application holds no secret, and nobody else can make one. An unbound token probes the request from the
monitor on; a bound one from enrichment on, once the caller is known, and only for that user (compared
without regard to case).

**A token that fails any check is ignored**: the request goes on exactly as if none had been sent — no
exception, no change to the response. The refusal is logged at Warning, naming the token's id where it can be
read so support can find the issue in Watch, and once per token: a client still sending an expired one logs
a Warning once, then Debug.

Without the Watch provider there is no key, and the header is ignored. `options.Probe = false` turns the probe
off. Like the correlation id, the probe attaches to the request's `Activity`; it is never sent on to the
services the request calls.

## The category

Everything here is logged as `Pondhawk.Diagnostics.Http` — deliberately under no application's namespace, so a
standing switch on an application's own prefix does not turn the request dump on by accident. It takes its own
switch: with [`Pondhawk.Logging.Watch`](../Pondhawk.Logging.Watch/README.md), add a switch for
`Pondhawk.Diagnostics.Http` at Debug to the domain; with plain `Microsoft.Extensions.Logging`,
`"Logging": { "LogLevel": { "Pondhawk.Diagnostics.Http": "Debug" } }`.

At Debug you get all of it — Begin, End and the dump. `EndLevelByOutcome` is for the application that wants
one line per request at its normal level without the rest.

ASP.NET Core's own request logging (`Microsoft.AspNetCore.Hosting.Diagnostics`, at Information) is separate
from this package and writes the full URL, query string included.
