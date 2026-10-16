// Copyright (c) Pond Hawk Technologies Inc. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System.Net;
using System.Security.Claims;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Pondhawk.Logging.AspNetCore.Tests.Support;
using Shouldly;
using Xunit;

namespace Pondhawk.Logging.AspNetCore.Tests;

/// <summary>
/// Tests the diagnostic probe end to end: a token Watch signed probes a request, anything else is refused and the
/// request goes on as if none was sent. The switch table is stood in for by a filter at Warning that a probe
/// lifts, as the Watch provider's does.
/// </summary>
public class DiagnosticProbeTests : IDisposable
{
    private const string Domain = "Orders";
    private const string Header = "X-Diagnostics-Probe";

    private readonly ECDsa _watch = ECDsa.Create(ECCurve.NamedCurves.nistP256);
    private readonly ProbeKeys _keys;

    public DiagnosticProbeTests() => _keys = new ProbeKeys { Current = (Domain, _watch.ExportSubjectPublicKeyInfo()) };

    public void Dispose()
    {
        _watch.Dispose();
        GC.SuppressFinalize(this);
    }

    // Stands in for the Watch provider's key source: the current key, or null before Watch has sent one.
    private sealed class ProbeKeys
    {
        public (string Domain, ReadOnlyMemory<byte> PublicKey)? Current { get; set; }
    }

    // What Watch issues: base64url(payload) "." base64url(ECDSA P-256 signature over the payload's base64url text).
    private string Token(string subject = null, TimeSpan? expiresIn = null, string domain = Domain, int version = 1, ECDsa signer = null)
    {
        var payload = JsonSerializer.Serialize(new Dictionary<string, object>
        {
            ["v"] = version,
            ["i"] = "tok-" + Guid.NewGuid().ToString("N"),
            ["d"] = domain,
            ["s"] = subject,
            ["x"] = DateTimeOffset.UtcNow.Add(expiresIn ?? TimeSpan.FromHours(1)).ToUnixTimeSeconds(),
        });

        return Signed(Base64Url(Encoding.UTF8.GetBytes(payload)), signer);
    }

    private string Signed(string encodedPayload, ECDsa signer = null) =>
        encodedPayload + "." + Base64Url((signer ?? _watch).SignData(Encoding.ASCII.GetBytes(encodedPayload), HashAlgorithmName.SHA256));

    private static string Base64Url(byte[] bytes) => Convert.ToBase64String(bytes).TrimEnd('=').Replace('+', '-').Replace('/', '_');

    private Task<DiagnosticsApp> Start(bool registerKeys = true) => DiagnosticsApp.StartAsync(
        app =>
        {
            app.UseDiagnosticsMonitor();
            app.Use((HttpContext context, RequestDelegate next) =>
            {
                // Signs the caller in, as authentication would, from a test header.
                if (context.Request.Headers.TryGetValue("X-Test-User", out var user))
                    context.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("preferred_username", user.ToString())], "test"));
                return next(context);
            });
            app.UseDiagnosticsEnrichment();
            app.UseRequestLogging();
            app.Run(context =>
            {
                context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger("Orders.Endpoint").LogTrace("deep in the endpoint");
                return context.Response.WriteAsync("ok");
            });
        },
        log: new CapturingLoggerProvider { DiagnosticsLevel = LogLevel.Trace },
        logging: builder => builder.AddFilter((_, level) => level >= LogLevel.Warning || DiagnosticProbe.IsActive),
        services: registerKeys
            ? services => services.AddKeyedSingleton<Func<(string Domain, ReadOnlyMemory<byte> PublicKey)?>>("Pondhawk.DiagnosticProbeKey", (_, _) => () => _keys.Current)
            : null);

    private static async Task<HttpResponseMessage> Get(DiagnosticsApp app, string token = null, string user = null)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/orders/7");
        if (token is not null)
            request.Headers.TryAddWithoutValidation(Header, token);
        if (user is not null)
            request.Headers.Add("X-Test-User", user);

        return await app.Client.SendAsync(request);
    }

    private static bool Probed(DiagnosticsApp app) => app.Log.Entries.Any(e => e.Message == "deep in the endpoint");

    private static IEnumerable<CapturingLoggerProvider.Entry> Refusals(DiagnosticsApp app) =>
        app.Log.Diagnostics.Where(e => e.Message.StartsWith("Diagnostic probe refused", StringComparison.Ordinal));

    [Fact]
    public async Task An_unbound_token_probes_the_whole_request_from_the_monitor()
    {
        await using var app = await Start();

        await Get(app, Token());

        Probed(app).ShouldBeTrue();
        app.Log.Diagnostics[0].Message.ShouldStartWith("Diagnostic probe tok-");
        app.Log.Diagnostics[1].Message.ShouldBe("Begin GET /orders/7");
        app.Log.Diagnostics.ShouldContain(e => e.Message == "HTTP GET /orders/7");   // the dump, with its switch off
        app.Log.Diagnostics[^1].Message.ShouldStartWith("End GET /orders/7 -> 200");
    }

    [Fact]
    public async Task A_token_bound_to_a_user_probes_that_users_requests_from_enrichment()
    {
        await using var app = await Start();

        await Get(app, Token(subject: "Pat"), user: "pat");

        Probed(app).ShouldBeTrue();
        app.Log.Diagnostics.ShouldNotContain(e => e.Message.StartsWith("Begin", StringComparison.Ordinal));   // before the user is known
        app.Log.Diagnostics[^1].Message.ShouldStartWith("End GET /orders/7 -> 200");
    }

    [Fact]
    public async Task A_token_bound_to_another_user_is_refused()
    {
        await using var app = await Start();

        await Get(app, Token(subject: "pat"), user: "sam");

        Probed(app).ShouldBeFalse();
        Refusals(app).ShouldHaveSingleItem().Message.ShouldContain("for another user");
    }

    public static TheoryData<string, string> Refused() => new()
    {
        { "expired", "expired" },
        { "too-distant", "expiry too far ahead" },
        { "other-domain", "for another domain" },
        { "unknown-version", "unknown version" },
        { "tampered", "bad signature" },
        { "other-signer", "bad signature" },
        { "fabrica", "malformed" },
        { "not-base64", "malformed" },
        { "not-json", "malformed" },
        { "oversized", "too long" },
    };

    [Theory]
    [MemberData(nameof(Refused))]
    public async Task A_refused_token_is_warned_about_once_and_the_request_goes_on_as_if_none_was_sent(string kind, string reason)
    {
        using var stranger = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var token = kind switch
        {
            "expired" => Token(expiresIn: TimeSpan.FromMinutes(-5)),
            "too-distant" => Token(expiresIn: TimeSpan.FromHours(5)),
            "other-domain" => Token(domain: "Billing"),
            "unknown-version" => Token(version: 2),
            "tampered" => Base64Url(Encoding.UTF8.GetBytes("""{"v":1,"d":"Orders","x":9999999999}""")) + Token()[Token().IndexOf('.', StringComparison.Ordinal)..],
            "other-signer" => Token(signer: stranger),
            "fabrica" => "1-" + Guid.NewGuid().ToString("N"),
            "not-base64" => "!!!." + Guid.NewGuid().ToString("N"),
            "not-json" => Signed(Base64Url(Encoding.UTF8.GetBytes("not json " + Guid.NewGuid()))),
            "oversized" => new string('a', 2000) + Guid.NewGuid().ToString("N"),
            _ => throw new ArgumentOutOfRangeException(nameof(kind)),
        };

        await using var app = await Start();

        var plain = await Get(app);
        var responses = new List<HttpResponseMessage>();
        for (var i = 0; i < 3; i++)
            responses.Add(await Get(app, token));

        Probed(app).ShouldBeFalse();
        var warning = Refusals(app).Where(e => e.Level == LogLevel.Warning).ShouldHaveSingleItem();
        warning.Message.ShouldContain(reason);

        foreach (var response in responses)
        {
            response.StatusCode.ShouldBe(plain.StatusCode);
            (await response.Content.ReadAsStringAsync()).ShouldBe(await plain.Content.ReadAsStringAsync());
        }
    }

    [Fact]
    public async Task A_refusal_names_the_token_id_so_support_can_find_the_issue()
    {
        await using var app = await Start();
        var token = Token(expiresIn: TimeSpan.FromMinutes(-5));
        var id = JsonDocument.Parse(Convert.FromBase64String(Pad(token[..token.IndexOf('.', StringComparison.Ordinal)]))).RootElement.GetProperty("i").GetString();

        await Get(app, token);

        Refusals(app).ShouldHaveSingleItem().Message.ShouldContain(id);
    }

    [Fact]
    public async Task Before_Watch_has_delivered_a_key_a_token_is_refused_and_after_it_is_accepted()
    {
        var key = _keys.Current;
        _keys.Current = null;
        await using var app = await Start();
        var token = Token();

        await Get(app, token);
        Probed(app).ShouldBeFalse();
        Refusals(app).ShouldHaveSingleItem().Message.ShouldContain("no probe key from Watch yet");

        _keys.Current = key;
        await Get(app, token);
        Probed(app).ShouldBeTrue();
    }

    [Fact]
    public async Task Without_a_key_source_the_probe_is_off_and_nothing_is_refused()
    {
        await using var app = await Start(registerKeys: false);

        var response = await Get(app, Token());

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        Probed(app).ShouldBeFalse();
        Refusals(app).ShouldBeEmpty();
    }

    [Fact]
    public async Task A_request_without_a_token_is_not_probed()
    {
        await using var app = await Start();

        await Get(app);

        Probed(app).ShouldBeFalse();
        Refusals(app).ShouldBeEmpty();
    }

    private static string Pad(string base64Url)
    {
        var text = base64Url.Replace('-', '+').Replace('_', '/');
        return text + new string('=', (4 - (text.Length % 4)) % 4);
    }
}
