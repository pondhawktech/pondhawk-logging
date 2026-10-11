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
    options.MaskedFormFields.Add("password");
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
| `UseDiagnosticsMonitor` | Early — first, or right after forwarded headers | Settles the correlation id, then logs `Begin GET /orders/7` and `End GET /orders/7 -> 200 (12.4ms)` at Debug. |
| `UseDiagnosticsEnrichment` | After authentication | Sets the subject (from the principal) and the tenant (from your delegate) for everything logged within the request. |
| `UseRequestLogging` | Deep — after routing and authentication | When the category is Debug-enabled, logs the whole request as a JSON payload, then `HTTP GET /orders/7 -> 200`. |

**Why the monitor and the request logger are two things.** They differ by position, and neither position does
both jobs. The monitor is the outer bracket: registered first, it times the whole pipeline and still closes a
request that authentication refuses or that throws — but that early there is no route and no user. The request
logger sits deep, where the request is fully resolved, and accepts that anything short-circuited before it
never arrives. The monitor logs the path only, never the query string.

A request that throws past the monitor is closed as a 500, since that is what the client is sent.

### Correlation id

Taken from the inbound `X-Correlation-Id` when it is well formed, otherwise the ambient one, otherwise a fresh
one; set with `CorrelationManager.Set` and echoed on the response so a caller can quote it. An id from outside
is accepted only if it is 1–64 ASCII letters, digits, `-`, `_` or `.` — a public server can be sent anything,
and the value lands on every event of the request. Anything else is ignored and replaced.

Whichever of the monitor and enrichment runs first settles it, so with the monitor in place a request refused
before authentication has a correlation id too.

### Subject and tenant

The subject defaults to the `sub` claim, then the name identifier claim; `options.Subject` replaces that. The
tenant comes from `options.Tenant` and is not set without it. Both go through `CorrelationManager`, stay in the
process, and are stamped on each event by the sink (Watch, CloudWatch).

All three values attach to the request's `Activity`. ASP.NET Core starts one whenever its own logging or a
diagnostic listener is enabled; without one there is nothing to attach them to, and the request goes on
without them.

## What the request dump shows

**The whole request.** This is a diagnostics tool, and hidden data defeats it. The dump is gated twice — the
application must call `UseRequestLogging`, and someone must switch `Pondhawk.Diagnostics.Http` to Debug — and
when it is on it shows what was sent, as one JSON payload (`LogJson`):

```json
{
  "Method": "POST",
  "Url": "/orders?returnUrl=%2Fcart&tag=a&tag=b",
  "Query": { "returnUrl": "/cart", "tag": ["a", "b"] },
  "Headers": {
    "Content-Type": "application/json",
    "Authorization": "Bearer ***(11)",
    "Cookie": "***(14)"
  },
  "Body": { "name": "widget", "lines": [ { "sku": "A1", "qty": 2 } ] }
}
```

JSON rather than text because a viewer can do more with it: Watch indents it, colours it and folds it, so the
headers collapse out of the way and a JSON body reads as JSON instead of one long line.

- **`Url`** — the path and query string exactly as sent, so an encoding problem shows.
- **`Query`** — the same query decoded, one property per parameter; a name sent more than once is an array.
  Left out when there is no query string.
- **`Headers`** — in full, except `Authorization` (the scheme is kept) and `Cookie`, whose values are replaced
  by their length. They are replayable credentials, and the scheme and length say most of what the value would.
- **`Body`** — what was sent, up to `MaxBodyBytes`: a JSON body as nested JSON, a form post as an object of its
  decoded fields, any other text (XML, `text/*`) as a string. A body that claims to be JSON and does not parse
  is written as the text it is. The body is buffered and rewound, so the endpoint reads it from the start.

What is said *about* a body goes beside it, never inside it:

| Property | When | Holds |
|---|---|---|
| `BodyOmitted` | The body is not text (multipart, binary), or `CaptureBody` is off. It is not read. | `ContentType`, `Length` |
| `BodyCut` | The body ran past `MaxBodyBytes`. `Body` is then the text that was read, as a string. | `Written`, `Length` |
| `BodyError` | The body could not be read. | The error message |

**Masking is by name, and yours to choose.** Nothing above is hidden unless you name it:

```csharp
options.MaskedFormFields.Add("password");        // fields of a form-urlencoded body
options.MaskedQueryParameters.Add("code");       // query parameters
options.MaskedHeaders.Add("X-Api-Key");          // headers — or .Clear() to show Authorization and Cookie
```

A masked value is replaced by its length as sent (`***(7)`), so an empty or truncated one still shows; a
masked query parameter is masked in both `Url` and `Query`. Names match without regard to case. `MaskedFormFields` covers form posts only: **a secret inside a JSON body is written as
sent**. If an application takes passwords or client secrets, name those fields — with the defaults, a sign-in
form post is logged with its password whenever the category is at Debug.

When the category is not Debug-enabled the request logger does nothing at all: the body is neither read nor
buffered.

## Options

| Option | Default | Purpose |
|---|---|---|
| `CorrelationHeader` | `X-Correlation-Id` | The inbound and echoed header. |
| `EchoCorrelationId` | `true` | Write the id on the response. |
| `Subject` | `sub`, then name identifier | `Func<HttpContext, string?>`. |
| `Tenant` | none | `Func<HttpContext, string?>`. |
| `Skip` | none | `Func<HttpContext, bool>`; true leaves a request out of the monitor and the dump. It still gets its correlation id, subject and tenant. |
| `MaskedHeaders` | `Authorization`, `Cookie` | Headers masked in the dump. Add to it or clear it. |
| `MaskedQueryParameters` | empty | Query parameters masked in the dump. |
| `MaskedFormFields` | empty | Form fields masked in the dump. |
| `CaptureBody` | `true` | False describes every body without reading it. |
| `MaxBodyBytes` | 65536 | How much of a body is written; at most 1 MB. |
| `EndLevelByOutcome` | `false` | Log the monitor's End line at Information, Warning for 401/403/429 and Error for 5xx, instead of Debug. |

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
