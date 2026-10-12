// Copyright (c) Pond Hawk Technologies Inc. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System.Diagnostics;
using System.Net;
using System.Security.Claims;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Pondhawk.Logging.AspNetCore.Tests.Support;
using Shouldly;
using Xunit;

namespace Pondhawk.Logging.AspNetCore.Tests;

/// <summary>Tests the correlation id, subject and tenant a request's events carry.</summary>
public class DiagnosticsEnrichmentTests
{
    private const string EndpointCategory = "Test.Endpoint";

    // Enrichment, then an endpoint that logs one event — the event a sink would stamp.
    private static Action<IApplicationBuilder> Enriched(params Claim[] claims) => app =>
    {
        app.Use((HttpContext context, RequestDelegate next) =>
        {
            if (claims.Length > 0)
                context.User = new ClaimsPrincipal(new ClaimsIdentity(claims, "test"));

            return next(context);
        });
        app.UseDiagnosticsEnrichment();
        app.Run(context =>
        {
            context.RequestServices.GetRequiredService<ILoggerFactory>().CreateLogger(EndpointCategory).LogInformation("handled");
            return Task.CompletedTask;
        });
    };

    private static CapturingLoggerProvider.Entry Handled(DiagnosticsApp app) =>
        app.Log.Entries.Single(e => e.Category == EndpointCategory);

    private static async Task<HttpResponseMessage> GetAsync(DiagnosticsApp app, string correlationId = null, string header = "X-Correlation-Id")
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, "/");

        if (correlationId is not null)
            request.Headers.TryAddWithoutValidation(header, correlationId);

        return await app.Client.SendAsync(request);
    }

    [Fact]
    public async Task A_well_formed_inbound_correlation_id_is_used_and_echoed()
    {
        await using var app = await DiagnosticsApp.StartAsync(Enriched());

        var response = await GetAsync(app, "Order-7_retry.2");

        Handled(app).CorrelationId.ShouldBe("Order-7_retry.2");
        response.Headers.GetValues("X-Correlation-Id").ShouldBe(["Order-7_retry.2"]);
    }

    [Theory]
    [InlineData("has a space")]
    [InlineData("<script>alert(1)</script>")]
    [InlineData("line\tbreak")]
    [InlineData("naïve")]
    [InlineData("aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")] // 65 characters
    public async Task A_malformed_or_over_long_inbound_correlation_id_is_replaced(string inbound)
    {
        await using var app = await DiagnosticsApp.StartAsync(Enriched());

        var response = await GetAsync(app, inbound);

        var used = Handled(app).CorrelationId;
        used.ShouldNotBe(inbound);
        RequestCorrelation.IsWellFormed(used).ShouldBeTrue();
        response.Headers.GetValues("X-Correlation-Id").ShouldBe([used]);
    }

    [Fact]
    public async Task With_no_inbound_id_a_fresh_one_is_made_and_each_request_gets_its_own()
    {
        await using var app = await DiagnosticsApp.StartAsync(Enriched());

        var first = (await GetAsync(app)).Headers.GetValues("X-Correlation-Id").Single();
        var second = (await GetAsync(app)).Headers.GetValues("X-Correlation-Id").Single();

        first.ShouldNotBeNullOrEmpty();
        second.ShouldNotBe(first);
    }

    [Fact]
    public async Task The_header_name_is_configurable_and_the_echo_can_be_turned_off()
    {
        await using var app = await DiagnosticsApp.StartAsync(Enriched(), options =>
        {
            options.CorrelationHeader = "X-Request-Id";
            options.EchoCorrelationId = false;
        });

        var response = await GetAsync(app, "abc123", "X-Request-Id");

        Handled(app).CorrelationId.ShouldBe("abc123");
        response.Headers.Contains("X-Request-Id").ShouldBeFalse();
    }

    [Fact]
    public async Task The_echo_survives_an_exception_handler_clearing_the_response()
    {
        await using var app = await DiagnosticsApp.StartAsync(app =>
        {
            app.UseExceptionHandler(new ExceptionHandlerOptions
            {
                ExceptionHandler = context =>
                {
                    context.Response.StatusCode = StatusCodes.Status500InternalServerError;
                    return Task.CompletedTask;
                },
            });
            app.UseDiagnosticsEnrichment();
            app.Run(_ => throw new InvalidOperationException("boom"));
        });

        var response = await GetAsync(app, "caller-id-2");

        response.StatusCode.ShouldBe(HttpStatusCode.InternalServerError);
        response.Headers.GetValues("X-Correlation-Id").ShouldBe(["caller-id-2"]);
    }

    [Fact]
    public async Task The_subject_is_the_sub_claim_as_a_last_resort_and_the_tenant_comes_from_the_delegate()
    {
        await using var app = await DiagnosticsApp.StartAsync(
            Enriched(new Claim("sub", "user-42"), new Claim(ClaimTypes.NameIdentifier, "other")),
            options => options.Tenant = context => context.Request.Headers["X-Realm"].ToString());

        using var request = new HttpRequestMessage(HttpMethod.Get, "/");
        request.Headers.Add("X-Realm", "acme");
        await app.Client.SendAsync(request);

        Handled(app).Subject.ShouldBe("user-42");
        Handled(app).Tenant.ShouldBe("acme");
    }

    [Theory]
    [InlineData(ClaimTypes.Name)]
    [InlineData("name")]
    public async Task The_subject_is_the_name_ahead_of_the_sub_claim(string nameClaim)
    {
        await using var app = await DiagnosticsApp.StartAsync(Enriched(new Claim("sub", "0b9f6c1e-5d2a-4c3b-9a55-1f2e3d4c5b6a"), new Claim(nameClaim, "pat")));

        await GetAsync(app);

        Handled(app).Subject.ShouldBe("pat");
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    public async Task The_subject_is_the_client_id_when_the_name_is_blank(string name)
    {
        Claim[] claims = [new Claim("sub", "0b9f6c1e-5d2a-4c3b-9a55-1f2e3d4c5b6a"), new Claim("client_id", "billing-worker")];

        await using var app = await DiagnosticsApp.StartAsync(Enriched(name is null ? claims : [.. claims, new Claim("name", name)]));

        await GetAsync(app);

        Handled(app).Subject.ShouldBe("billing-worker");
    }

    [Fact]
    public async Task The_subject_falls_back_to_the_name_identifier_claim()
    {
        await using var app = await DiagnosticsApp.StartAsync(Enriched(new Claim(ClaimTypes.NameIdentifier, "user-9")));

        await GetAsync(app);

        Handled(app).Subject.ShouldBe("user-9");
    }

    [Fact]
    public async Task The_subject_delegate_overrides_the_default()
    {
        await using var app = await DiagnosticsApp.StartAsync(
            Enriched(new Claim("sub", "user-42"), new Claim("email", "pat@example.com")),
            options => options.Subject = context => context.User.FindFirst("email")?.Value);

        await GetAsync(app);

        Handled(app).Subject.ShouldBe("pat@example.com");
    }

    [Fact]
    public async Task An_anonymous_request_has_no_subject_or_tenant()
    {
        await using var app = await DiagnosticsApp.StartAsync(Enriched());

        await GetAsync(app);

        Handled(app).Subject.ShouldBeNull();
        Handled(app).Tenant.ShouldBeNull();
    }

    [Fact]
    public async Task With_no_activity_nothing_throws_and_the_request_completes()
    {
        Activity seen = new("placeholder");

        // With only the diagnostics category enabled, ASP.NET Core starts no Activity for the request.
        var log = new CapturingLoggerProvider { OtherCategories = false };

        await using var app = await DiagnosticsApp.StartAsync(
            app =>
            {
                app.UseDiagnosticsMonitor();
                app.Use((HttpContext context, RequestDelegate next) =>
                {
                    context.User = new ClaimsPrincipal(new ClaimsIdentity([new Claim("sub", "user-42")], "test"));
                    return next(context);
                });
                app.UseDiagnosticsEnrichment();
                app.UseRequestLogging();
                app.Run(context =>
                {
                    seen = Activity.Current;
                    context.Response.StatusCode = StatusCodes.Status202Accepted;
                    return Task.CompletedTask;
                });
            },
            options => options.Tenant = _ => "acme",
            log);

        var response = await GetAsync(app, "caller-id-3");

        seen.ShouldBeNull();
        response.StatusCode.ShouldBe(HttpStatusCode.Accepted);
        response.Headers.GetValues("X-Correlation-Id").ShouldBe(["caller-id-3"]);
        log.Diagnostics.Select(e => e.Message).ShouldContain("Begin GET /");
        log.Diagnostics.ShouldAllBe(e => e.CorrelationId == null && e.Subject == null);
    }
}
