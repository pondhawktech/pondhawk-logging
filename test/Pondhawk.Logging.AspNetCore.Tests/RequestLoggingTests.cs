// Copyright (c) Pond Hawk Technologies Inc. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
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
    public async Task A_masked_form_field_never_appears_and_its_neighbours_are_untouched()
    {
        var seen = new Seen();
        await using var app = await DiagnosticsApp.StartAsync(Dumping(seen), options =>
        {
            options.MaskedFormFields.Add("password");
            options.MaskedFormFields.Add("client_secret");
        });

        await app.Client.PostAsync("/signin", Content("username=pat&Password=hunter2&client_secret=abc%20def&remember=on", Form));

        app.Log.Everything.ShouldNotContain("hunter2");
        app.Log.Everything.ShouldNotContain("abc%20def");
        app.Log.Everything.ShouldNotContain("abc def");

        var body = Dump(app).GetProperty("Body");
        body.EnumerateObject().Select(p => p.Name).ShouldBe(["username", "Password", "client_secret", "remember"]);
        Text(body, "username").ShouldBe("pat");
        Text(body, "Password").ShouldBe("***(7)");
        Text(body, "client_secret").ShouldBe("***(9)");
        Text(body, "remember").ShouldBe("on");

        // Masked in the log only: the endpoint still gets what was sent.
        seen.Body.ShouldBe("username=pat&Password=hunter2&client_secret=abc%20def&remember=on");
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
        Text(headers, "Cookie").ShouldBe("***(14)");
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
}
