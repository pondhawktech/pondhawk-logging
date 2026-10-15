// Copyright (c) Pond Hawk Technologies Inc. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System.Net;
using System.Net.Http.Headers;
using System.Security.Claims;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Pondhawk.Logging.AspNetCore.Tests.Support;
using Shouldly;
using Xunit;

namespace Pondhawk.Logging.AspNetCore.Tests;

/// <summary>Tests the deep, opt-in dump: what it shows, what it masks, and that it leaves the request intact.</summary>
public class RequestLoggingTests
{
    private const string Json = "application/json";
    private const string Form = "application/x-www-form-urlencoded";

    // What the endpoint saw, so a test can tell the dump left the request as it found it.
    private sealed class Seen
    {
        public string Body { get; set; }

        public bool Buffered { get; set; }
    }

    private static Action<IApplicationBuilder> Dumping(Seen seen) => app =>
    {
        app.UseRequestLogging();
        app.Run(async context =>
        {
            seen.Buffered = context.Request.Body.CanSeek;

            using var reader = new StreamReader(context.Request.Body, Encoding.UTF8);
            seen.Body = await reader.ReadToEndAsync();

            context.Response.StatusCode = 201;
        });
    };

    private static StringContent Content(string body, string contentType) =>
        new(body, Encoding.UTF8, MediaTypeHeaderValue.Parse(contentType));

    // The dump is a JSON payload; the clone outlives the document it was parsed from.
    private static JsonElement Dump(DiagnosticsApp app)
    {
        using var document = JsonDocument.Parse(app.Log.Diagnostics[0].Payload);
        return document.RootElement.Clone();
    }

    // The second event: the response, described the same way.
    private static JsonElement Response(DiagnosticsApp app)
    {
        using var document = JsonDocument.Parse(app.Log.Diagnostics[1].Payload);
        return document.RootElement.Clone();
    }

    private static Action<IApplicationBuilder> Answering(RequestDelegate endpoint) => app =>
    {
        app.UseRequestLogging();
        app.Run(endpoint);
    };

    private static string Text(JsonElement element, params string[] path)
    {
        foreach (var name in path)
            element = element.GetProperty(name);

        return element.GetString();
    }

    [Fact]
    public async Task By_default_the_whole_request_is_written_then_the_status()
    {
        var seen = new Seen();
        await using var app = await DiagnosticsApp.StartAsync(Dumping(seen));

        using var request = new HttpRequestMessage(HttpMethod.Post, "/orders?code=abc123&return=%2Fcart+1&return=%2Fhome")
        {
            Content = Content("""{"name":"widget","lines":[{"sku":"A1","qty":2}]}""", Json),
        };
        request.Headers.Add("X-Normal", "visible");

        await app.Client.SendAsync(request);

        var events = app.Log.Diagnostics;
        events.Count.ShouldBe(2);
        events[0].Level.ShouldBe(LogLevel.Debug);
        events[0].Message.ShouldBe("HTTP POST /orders");
        events[1].Message.ShouldBe("HTTP POST /orders -> 201");

        var dump = Dump(app);
        dump.EnumerateObject().Select(p => p.Name).ShouldBe(["Method", "Url", "Query", "Headers", "Body"]);
        Text(dump, "Method").ShouldBe("POST");

        // The URL as sent; the query decoded, a repeated name as an array.
        Text(dump, "Url").ShouldBe("/orders?code=abc123&return=%2Fcart+1&return=%2Fhome");
        Text(dump, "Query", "code").ShouldBe("abc123");
        dump.GetProperty("Query").GetProperty("return").EnumerateArray().Select(v => v.GetString()).ShouldBe(["/cart 1", "/home"]);

        Text(dump, "Headers", "X-Normal").ShouldBe("visible");
        Text(dump, "Headers", "Content-Type").ShouldBe("application/json");

        // A JSON body is nested JSON, not a string holding JSON.
        dump.GetProperty("Body").ValueKind.ShouldBe(JsonValueKind.Object);
        Text(dump, "Body", "name").ShouldBe("widget");
        dump.GetProperty("Body").GetProperty("lines")[0].GetProperty("qty").GetInt32().ShouldBe(2);

        seen.Body.ShouldBe("""{"name":"widget","lines":[{"sku":"A1","qty":2}]}""");
    }

    [Fact]
    public async Task The_payload_is_typed_as_json_and_is_not_escaped_beyond_what_json_needs()
    {
        await using var app = await DiagnosticsApp.StartAsync(Dumping(new Seen()));

        await app.Client.GetAsync("/search?q=a+b&lang=fr");

        // Readable as logged: no \u0026 for the ampersand, no \u002B for the plus.
        app.Log.Diagnostics[0].Payload.ShouldContain("\"Url\":\"/search?q=a+b&lang=fr\"");
    }

    [Fact]
    public async Task By_default_a_form_body_is_written_in_full_as_its_fields()
    {
        var seen = new Seen();
        await using var app = await DiagnosticsApp.StartAsync(Dumping(seen));

        await app.Client.PostAsync("/signin", Content("username=pat+smith&password=hunter2&scope=a&scope=b", Form));

        var body = Dump(app).GetProperty("Body");
        Text(body, "username").ShouldBe("pat smith");
        Text(body, "password").ShouldBe("hunter2");
        body.GetProperty("scope").EnumerateArray().Select(v => v.GetString()).ShouldBe(["a", "b"]);
    }

    [Fact]
    public async Task A_text_body_that_is_not_json_is_written_as_a_string()
    {
        await using var app = await DiagnosticsApp.StartAsync(Dumping(new Seen()));

        await app.Client.PostAsync("/notes", Content("<note>hello & goodbye</note>", "application/xml"));

        Text(Dump(app), "Body").ShouldBe("<note>hello & goodbye</note>");
    }

    [Fact]
    public async Task A_body_that_claims_json_and_is_not_is_written_as_the_text_it_is()
    {
        var seen = new Seen();
        await using var app = await DiagnosticsApp.StartAsync(Dumping(seen));

        await app.Client.PostAsync("/orders", Content("""{"name": oops""", Json));

        Text(Dump(app), "Body").ShouldBe("""{"name": oops""");
        seen.Body.ShouldBe("""{"name": oops""");
    }

    [Fact]
    public async Task Nothing_is_logged_and_the_body_is_not_buffered_when_the_category_is_not_debug_enabled()
    {
        var seen = new Seen();
        var log = new CapturingLoggerProvider { DiagnosticsLevel = LogLevel.Information };
        await using var app = await DiagnosticsApp.StartAsync(Dumping(seen), log: log);

        await app.Client.PostAsync("/orders", Content("""{"a":1}""", Json));

        log.Diagnostics.ShouldBeEmpty();
        seen.Buffered.ShouldBeFalse();
        seen.Body.ShouldBe("""{"a":1}""");
    }

    [Fact]
    public async Task A_skipped_request_is_not_dumped_and_its_body_is_not_buffered()
    {
        var seen = new Seen();
        await using var app = await DiagnosticsApp.StartAsync(
            Dumping(seen),
            options => options.Skip = context => context.Request.Path.StartsWithSegments("/health"));

        await app.Client.PostAsync("/health", Content("""{"secret":"s3cr3t"}""", Json));

        app.Log.Diagnostics.ShouldBeEmpty();
        seen.Buffered.ShouldBeFalse();
        seen.Body.ShouldBe("""{"secret":"s3cr3t"}""");
    }

    [Fact]
    public async Task A_masked_form_field_never_appears_and_its_neighbours_are_untouched()
    {
        var seen = new Seen();
        await using var app = await DiagnosticsApp.StartAsync(Dumping(seen), options =>
        {
            options.MaskedBodyFields.Add("password");
            options.MaskedBodyFields.Add("client_secret");
        });

        await app.Client.PostAsync("/signin", Content("username=pat&Password=hunter2&client_secret=abc%20def&remember=on", Form));

        app.Log.Everything.ShouldNotContain("hunter2");
        app.Log.Everything.ShouldNotContain("abc%20def");
        app.Log.Everything.ShouldNotContain("abc def");

        var body = Dump(app).GetProperty("Body");
        body.EnumerateObject().Select(p => p.Name).ShouldBe(["username", "Password", "client_secret", "remember"]);
        Text(body, "username").ShouldBe("pat");
        Text(body, "Password").ShouldBe("***(7)");
        Text(body, "client_secret").ShouldBe("***(7)");
        Text(body, "remember").ShouldBe("on");

        // Masked in the log only: the endpoint still gets what was sent.
        seen.Body.ShouldBe("username=pat&Password=hunter2&client_secret=abc%20def&remember=on");
    }

    // Authentication that handles the request itself (a token endpoint) parses the form before the dump runs.
    private static Action<IApplicationBuilder> ParsingTheFormFirst(Seen seen) => app =>
    {
        app.Use(async (context, next) =>
        {
            await context.Request.ReadFormAsync();
            await next(context);
        });

        Dumping(seen)(app);
    };

    [Fact]
    public async Task A_form_already_parsed_is_written_from_its_fields_and_masked_as_an_unparsed_one_is()
    {
        var seen = new Seen();
        await using var app = await DiagnosticsApp.StartAsync(ParsingTheFormFirst(seen), options => options.MaskedBodyFields.Add("client_secret"));

        await app.Client.PostAsync("/connect/token", Content("grant_type=client_credentials&client_secret=abc%20def&scope=a&scope=b", Form));

        // The stream really was spent, so the fields can only have come from the parsed form.
        seen.Buffered.ShouldBeFalse();
        seen.Body.ShouldBeEmpty();

        app.Log.Everything.ShouldNotContain("abc def");

        var dump = Dump(app);
        var body = dump.GetProperty("Body");
        body.EnumerateObject().Select(p => p.Name).ShouldBe(["grant_type", "client_secret", "scope"]);
        Text(body, "grant_type").ShouldBe("client_credentials");
        Text(body, "client_secret").ShouldBe("***(7)");
        body.GetProperty("scope").EnumerateArray().Select(v => v.GetString()).ShouldBe(["a", "b"]);
        dump.TryGetProperty("BodyCut", out _).ShouldBeFalse();
    }

    [Fact]
    public async Task A_form_already_parsed_is_cut_at_the_first_field_that_does_not_fit()
    {
        await using var app = await DiagnosticsApp.StartAsync(ParsingTheFormFirst(new Seen()), options => options.MaxBodyBytes = 10);

        await app.Client.PostAsync("/connect/token", Content("a=12345&bb=123456789&c=1", Form));

        var dump = Dump(app);
        dump.GetProperty("Body").EnumerateObject().Select(p => p.Name).ShouldBe(["a"]);
        dump.GetProperty("BodyCut").GetProperty("Written").GetInt32().ShouldBe(6);
        dump.GetProperty("BodyCut").GetProperty("Length").GetInt64().ShouldBe(24);
    }

    [Fact]
    public async Task A_masked_form_field_sent_bare_is_masked_as_an_empty_value()
    {
        await using var app = await DiagnosticsApp.StartAsync(Dumping(new Seen()), options => options.MaskedBodyFields.Add("password"));

        await app.Client.PostAsync("/signin", Content("username=pat&password", Form));

        Text(Dump(app), "Body", "password").ShouldBe("***(0)");
    }

    [Fact]
    public async Task A_masked_query_parameter_never_appears_in_the_url_or_the_query()
    {
        await using var app = await DiagnosticsApp.StartAsync(Dumping(new Seen()), options => options.MaskedQueryParameters.Add("code"));

        await app.Client.GetAsync("/callback?state=xyz&code=4%2F0AbCdEf&flag");

        app.Log.Everything.ShouldNotContain("0AbCdEf");

        var dump = Dump(app);
        Text(dump, "Url").ShouldBe("/callback?state=xyz&code=***(11)&flag");
        Text(dump, "Query", "state").ShouldBe("xyz");
        Text(dump, "Query", "code").ShouldBe("***(11)");
        Text(dump, "Query", "flag").ShouldBe("");
    }

    [Fact]
    public async Task Authorization_cookie_and_a_custom_header_are_masked_with_their_lengths()
    {
        await using var app = await DiagnosticsApp.StartAsync(Dumping(new Seen()), options => options.MaskedHeaders.Add("X-Api-Key"));

        using var request = new HttpRequestMessage(HttpMethod.Get, "/orders");
        request.Headers.TryAddWithoutValidation("Authorization", "Bearer abc.def.ghi");
        request.Headers.TryAddWithoutValidation("Cookie", "sid=0123456789");
        request.Headers.TryAddWithoutValidation("x-api-key", "key-12345");
        request.Headers.TryAddWithoutValidation("X-Normal", "visible");

        await app.Client.SendAsync(request);

        var headers = Dump(app).GetProperty("Headers");
        Text(headers, "Authorization").ShouldBe("Bearer ***(11)");
        Text(headers, "Cookie").ShouldBe("sid=***(10)");
        Text(headers, "x-api-key").ShouldBe("***(9)");
        Text(headers, "X-Normal").ShouldBe("visible");

        var everything = app.Log.Everything;
        everything.ShouldNotContain("abc.def.ghi");
        everything.ShouldNotContain("0123456789");
        everything.ShouldNotContain("key-12345");
    }

    [Fact]
    public async Task With_the_masked_headers_cleared_they_appear_in_full()
    {
        await using var app = await DiagnosticsApp.StartAsync(Dumping(new Seen()), options => options.MaskedHeaders.Clear());

        using var request = new HttpRequestMessage(HttpMethod.Get, "/orders");
        request.Headers.TryAddWithoutValidation("Authorization", "Bearer abc.def.ghi");
        request.Headers.TryAddWithoutValidation("Cookie", "sid=0123456789");

        await app.Client.SendAsync(request);

        var headers = Dump(app).GetProperty("Headers");
        Text(headers, "Authorization").ShouldBe("Bearer abc.def.ghi");
        Text(headers, "Cookie").ShouldBe("sid=0123456789");
    }

    [Fact]
    public async Task A_body_that_is_not_text_is_described_and_not_read()
    {
        var seen = new Seen();
        await using var app = await DiagnosticsApp.StartAsync(Dumping(seen));

        using var content = new ByteArrayContent([0x89, 0x50, 0x4E, 0x47, 0x00]);
        content.Headers.ContentType = new MediaTypeHeaderValue("image/png");

        await app.Client.PostAsync("/upload", content);

        var dump = Dump(app);
        dump.TryGetProperty("Body", out _).ShouldBeFalse();
        Text(dump, "BodyOmitted", "ContentType").ShouldBe("image/png");
        Text(dump, "BodyOmitted", "Reason").ShouldBe("not text");
        dump.GetProperty("BodyOmitted").GetProperty("Length").GetInt64().ShouldBe(5);
        seen.Buffered.ShouldBeFalse();
    }

    [Fact]
    public async Task With_capture_off_every_body_is_described_and_not_read()
    {
        var seen = new Seen();
        await using var app = await DiagnosticsApp.StartAsync(Dumping(seen), options => options.CaptureBody = false);

        await app.Client.PostAsync("/signin", Content("username=pat&password=hunter2", Form));

        app.Log.Everything.ShouldNotContain("hunter2");

        var dump = Dump(app);
        dump.TryGetProperty("Body", out _).ShouldBeFalse();
        Text(dump, "BodyOmitted", "ContentType").ShouldBe(Form);
        Text(dump, "BodyOmitted", "Reason").ShouldBe("capture is off");
        dump.GetProperty("BodyOmitted").GetProperty("Length").GetInt64().ShouldBe(29);
        seen.Buffered.ShouldBeFalse();
        seen.Body.ShouldBe("username=pat&password=hunter2");
    }

    [Fact]
    public async Task A_body_over_the_cap_is_cut_and_says_so_and_the_endpoint_still_reads_all_of_it()
    {
        var seen = new Seen();
        await using var app = await DiagnosticsApp.StartAsync(Dumping(seen), options => options.MaxBodyBytes = 10);

        var body = "0123456789ABCDEFGHIJ";
        await app.Client.PostAsync("/orders", Content(body, "text/plain"));

        var dump = Dump(app);
        Text(dump, "Body").ShouldBe("0123456789");
        dump.GetProperty("BodyCut").GetProperty("Written").GetInt32().ShouldBe(10);
        dump.GetProperty("BodyCut").GetProperty("Length").GetInt64().ShouldBe(20);
        app.Log.Everything.ShouldNotContain("ABCDEFGHIJ");
        seen.Body.ShouldBe(body);
    }

    [Fact]
    public async Task A_json_body_over_the_cap_is_written_as_the_text_that_was_read()
    {
        var seen = new Seen();
        await using var app = await DiagnosticsApp.StartAsync(Dumping(seen), options => options.MaxBodyBytes = 12);

        var body = """{"name":"widget","qty":2}""";
        await app.Client.PostAsync("/orders", Content(body, Json));

        var dump = Dump(app);
        Text(dump, "Body").ShouldBe("""{"name":"wid""");
        dump.GetProperty("BodyCut").GetProperty("Written").GetInt32().ShouldBe(12);
        seen.Body.ShouldBe(body);
    }

    [Fact]
    public async Task A_body_exactly_at_the_cap_is_not_marked_as_cut()
    {
        var seen = new Seen();
        await using var app = await DiagnosticsApp.StartAsync(Dumping(seen), options => options.MaxBodyBytes = 10);

        await app.Client.PostAsync("/orders", Content("0123456789", "text/plain"));

        var dump = Dump(app);
        Text(dump, "Body").ShouldBe("0123456789");
        dump.TryGetProperty("BodyCut", out _).ShouldBeFalse();
        seen.Body.ShouldBe("0123456789");
    }

    [Fact]
    public async Task A_request_with_no_body_or_query_has_neither()
    {
        await using var app = await DiagnosticsApp.StartAsync(Dumping(new Seen()));

        await app.Client.GetAsync("/orders");

        var dump = Dump(app);
        dump.EnumerateObject().Select(p => p.Name).ShouldBe(["Method", "Url", "Headers"]);
        Text(dump, "Url").ShouldBe("/orders");
    }

    [Fact]
    public async Task A_compressed_body_is_described_and_not_read()
    {
        var seen = new Seen();
        await using var app = await DiagnosticsApp.StartAsync(Dumping(seen));

        using var content = new ByteArrayContent([0x1F, 0x8B, 0x08, 0x00]);
        content.Headers.ContentType = new MediaTypeHeaderValue(Json);
        content.Headers.ContentEncoding.Add("gzip");

        await app.Client.PostAsync("/orders", content);

        Text(Dump(app), "BodyOmitted", "Reason").ShouldBe("content-encoded");
        seen.Buffered.ShouldBeFalse();
    }

    [Fact]
    public async Task A_masked_field_in_a_json_body_never_appears_at_any_depth()
    {
        var seen = new Seen();
        await using var app = await DiagnosticsApp.StartAsync(Dumping(seen), options =>
        {
            options.MaskedBodyFields.Add("password");
            options.MaskedBodyFields.Add("pin");
            options.MaskedBodyFields.Add("card");
        });

        var body = """{"user":"pat","Password":"hunter2","pin":4321,"card":{"number":"4111-secret"},"contacts":[{"name":"sam","password":"s3cr3t!"}],"note":"password"}""";
        await app.Client.PostAsync("/signin", Content(body, Json));

        var everything = app.Log.Everything;
        everything.ShouldNotContain("hunter2");
        everything.ShouldNotContain("4321");
        everything.ShouldNotContain("4111-secret");
        everything.ShouldNotContain("s3cr3t!");

        var logged = Dump(app).GetProperty("Body");
        Text(logged, "user").ShouldBe("pat");
        Text(logged, "Password").ShouldBe("***(7)");
        Text(logged, "pin").ShouldBe("***");
        Text(logged, "card").ShouldBe("***");
        Text(logged.GetProperty("contacts")[0], "name").ShouldBe("sam");
        Text(logged.GetProperty("contacts")[0], "password").ShouldBe("***(7)");

        // Masked by property name, not by what a value says.
        Text(logged, "note").ShouldBe("password");

        seen.Body.ShouldBe(body);
    }

    [Theory]
    [InlineData("""{"password":"hunter2", oops""", 65536)]   // malformed
    [InlineData("""{"user":"pat","password":"hunter2"}""", 30)]   // cut short of its end
    public async Task A_json_body_that_cannot_be_parsed_is_left_out_when_there_are_fields_to_mask(string body, int cap)
    {
        await using var app = await DiagnosticsApp.StartAsync(Dumping(new Seen()), options =>
        {
            options.MaskedBodyFields.Add("password");
            options.MaxBodyBytes = cap;
        });

        await app.Client.PostAsync("/signin", Content(body, Json));

        app.Log.Everything.ShouldNotContain("hunter2");

        var dump = Dump(app);
        dump.TryGetProperty("Body", out _).ShouldBeFalse();
        Text(dump, "BodyOmitted", "Reason").ShouldBe("not valid JSON, so the masked fields could not be found");
    }

    [Fact]
    public async Task The_endpoint_and_the_user_the_request_resolved_to_are_written()
    {
        await using var app = await DiagnosticsApp.StartAsync(app =>
        {
            app.UseRouting();
            app.Use((HttpContext context, RequestDelegate next) =>
            {
                context.User = new ClaimsPrincipal(new ClaimsIdentity(
                    [new Claim(ClaimTypes.Name, "pat"), new Claim("sub", "user-42")], "Bearer"));
                return next(context);
            });
            app.UseRequestLogging();
            app.UseEndpoints(endpoints => endpoints.MapGet("/orders/{id:int}/lines/{line}", () => "ok").WithDisplayName("Get order line"));
        });

        await app.Client.GetAsync("/orders/7/lines/a1");

        var dump = Dump(app);
        dump.EnumerateObject().Select(p => p.Name).ShouldBe(["Method", "Url", "Endpoint", "User", "Headers"]);

        Text(dump, "Endpoint", "Name").ShouldBe("Get order line");
        Text(dump, "Endpoint", "Route").ShouldBe("/orders/{id:int}/lines/{line}");
        Text(dump, "Endpoint", "Values", "id").ShouldBe("7");
        Text(dump, "Endpoint", "Values", "line").ShouldBe("a1");

        Text(dump, "User", "Name").ShouldBe("pat");
        Text(dump, "User", "Subject").ShouldBe("user-42");
        Text(dump, "User", "Scheme").ShouldBe("Bearer");
    }

    [Fact]
    public async Task An_anonymous_request_to_no_endpoint_has_neither_endpoint_nor_user()
    {
        await using var app = await DiagnosticsApp.StartAsync(Dumping(new Seen()));

        await app.Client.GetAsync("/orders");

        Dump(app).TryGetProperty("Endpoint", out _).ShouldBeFalse();
        Dump(app).TryGetProperty("User", out _).ShouldBeFalse();
    }

    [Fact]
    public async Task The_response_is_described_with_its_status_elapsed_time_and_headers()
    {
        await using var app = await DiagnosticsApp.StartAsync(Answering(context =>
        {
            context.Response.StatusCode = 302;
            context.Response.Headers.Location = "/signin?returnUrl=%2Forders";
            context.Response.Headers.Append("Set-Cookie", "sid=0123456789abcdef; Path=/; HttpOnly; SameSite=Lax");
            context.Response.Headers.Append("Set-Cookie", "theme=dark; Path=/");
            return Task.CompletedTask;
        }));

        await app.Client.GetAsync("/orders");

        app.Log.Diagnostics[1].Message.ShouldBe("HTTP GET /orders -> 302");

        var response = Response(app);
        response.EnumerateObject().Select(p => p.Name).ShouldBe(["Status", "Elapsed", "Headers"]);
        response.GetProperty("Status").GetInt32().ShouldBe(302);
        response.GetProperty("Elapsed").GetDouble().ShouldBeGreaterThanOrEqualTo(0);
        Text(response, "Headers", "Location").ShouldBe("/signin?returnUrl=%2Forders");

        // A cookie's name and attributes are worth reading; its value is the credential.
        response.GetProperty("Headers").GetProperty("Set-Cookie").EnumerateArray().Select(v => v.GetString())
            .ShouldBe(["sid=***(16); Path=/; HttpOnly; SameSite=Lax", "theme=***(4); Path=/"]);
        app.Log.Everything.ShouldNotContain("0123456789abcdef");
    }

    [Fact]
    public async Task The_response_body_is_not_kept_unless_asked_for()
    {
        await using var app = await DiagnosticsApp.StartAsync(Answering(context => context.Response.WriteAsJsonAsync(new { token = "tok-123" })));

        var sent = await app.Client.GetStringAsync("/token");

        sent.ShouldBe("""{"token":"tok-123"}""");
        Response(app).TryGetProperty("Body", out _).ShouldBeFalse();
        app.Log.Everything.ShouldNotContain("tok-123");
    }

    [Fact]
    public async Task A_captured_json_response_body_is_nested_and_masked_and_reaches_the_client_whole()
    {
        await using var app = await DiagnosticsApp.StartAsync(
            Answering(context => context.Response.WriteAsJsonAsync(new { access_token = "tok-123", expires_in = 3600, user = new { name = "pat" } })),
            options =>
            {
                options.CaptureResponseBody = true;
                options.MaskedBodyFields.Add("access_token");
            });

        var sent = await app.Client.GetStringAsync("/token");

        sent.ShouldBe("""{"access_token":"tok-123","expires_in":3600,"user":{"name":"pat"}}""");

        var body = Response(app).GetProperty("Body");
        Text(body, "access_token").ShouldBe("***(7)");
        body.GetProperty("expires_in").GetInt32().ShouldBe(3600);
        Text(body, "user", "name").ShouldBe("pat");
        app.Log.Everything.ShouldNotContain("tok-123");
    }

    [Fact]
    public async Task A_response_left_unflushed_in_the_pipe_still_reaches_the_client_and_the_log()
    {
        await using var app = await DiagnosticsApp.StartAsync(
            Answering(context =>
            {
                context.Response.ContentType = "text/plain";

                // Written into the response's pipe and never flushed: the pipe was made over the capturing stream.
                var text = "left in the pipe"u8;
                text.CopyTo(context.Response.BodyWriter.GetSpan(text.Length));
                context.Response.BodyWriter.Advance(text.Length);

                return Task.CompletedTask;
            }),
            options => options.CaptureResponseBody = true);

        var sent = await app.Client.GetStringAsync("/note");

        sent.ShouldBe("left in the pipe");
        Text(Response(app), "Body").ShouldBe("left in the pipe");
    }

    [Fact]
    public async Task A_captured_response_body_over_the_cap_is_cut_and_the_client_still_gets_all_of_it()
    {
        await using var app = await DiagnosticsApp.StartAsync(
            Answering(async context =>
            {
                context.Response.ContentType = "text/plain";
                await context.Response.WriteAsync("0123456789");
                await context.Response.WriteAsync("ABCDEFGHIJ");
            }),
            options =>
            {
                options.CaptureResponseBody = true;
                options.MaxBodyBytes = 12;
            });

        var sent = await app.Client.GetStringAsync("/note");

        sent.ShouldBe("0123456789ABCDEFGHIJ");

        var response = Response(app);
        Text(response, "Body").ShouldBe("0123456789AB");
        response.GetProperty("BodyCut").GetProperty("Written").GetInt32().ShouldBe(12);
        response.GetProperty("BodyCut").GetProperty("Length").GetInt64().ShouldBe(20);
    }

    [Fact]
    public async Task A_captured_response_body_that_is_not_text_is_described()
    {
        await using var app = await DiagnosticsApp.StartAsync(
            Answering(async context =>
            {
                context.Response.ContentType = "image/png";
                await context.Response.Body.WriteAsync(new byte[] { 0x89, 0x50, 0x4E, 0x47, 0x00 });
            }),
            options => options.CaptureResponseBody = true);

        var sent = await app.Client.GetByteArrayAsync("/logo");

        sent.Length.ShouldBe(5);

        var response = Response(app);
        response.TryGetProperty("Body", out _).ShouldBeFalse();
        Text(response, "BodyOmitted", "ContentType").ShouldBe("image/png");
        Text(response, "BodyOmitted", "Reason").ShouldBe("not text");
        response.GetProperty("BodyOmitted").GetProperty("Length").GetInt64().ShouldBe(5);
    }

    [Fact]
    public async Task With_response_capture_on_a_failing_request_is_still_the_exception_handlers_to_answer()
    {
        await using var app = await DiagnosticsApp.StartAsync(
            app =>
            {
                app.UseExceptionHandler(new ExceptionHandlerOptions
                {
                    ExceptionHandler = context =>
                    {
                        context.Response.StatusCode = StatusCodes.Status500InternalServerError;
                        return context.Response.WriteAsync("handled");
                    },
                });
                app.UseRequestLogging();
                app.Run(context =>
                {
                    context.Response.ContentType = "text/plain";

                    var text = "half an answer"u8;
                    text.CopyTo(context.Response.BodyWriter.GetSpan(text.Length));
                    context.Response.BodyWriter.Advance(text.Length);

                    throw new InvalidOperationException("boom");
                });
            },
            options => options.CaptureResponseBody = true);

        var response = await app.Client.GetAsync("/broken");

        response.StatusCode.ShouldBe(HttpStatusCode.InternalServerError);
        (await response.Content.ReadAsStringAsync()).ShouldBe("handled");

        // The request was dumped; the response that never was is not.
        app.Log.Diagnostics.Count(e => e.Message.StartsWith("HTTP GET /broken", StringComparison.Ordinal)).ShouldBe(1);
    }
}
