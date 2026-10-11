// Copyright (c) Pond Hawk Technologies Inc. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System.Net;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Pondhawk.Logging.AspNetCore.Tests.Support;
using Shouldly;
using Xunit;

namespace Pondhawk.Logging.AspNetCore.Tests;

/// <summary>Tests the early bracket: Begin and End around the whole pipeline, however the request ends.</summary>
public class DiagnosticsMonitorTests
{
    private static Action<IApplicationBuilder> Responding(int status) => app =>
    {
        app.UseDiagnosticsMonitor();
        app.Run(context =>
        {
            context.Response.StatusCode = status;
            return Task.CompletedTask;
        });
    };

    [Fact]
    public async Task A_request_logs_begin_and_end_with_status_and_elapsed_time()
    {
        await using var app = await DiagnosticsApp.StartAsync(Responding(204));

        await app.Client.GetAsync("/orders/7?code=secret-code");

        var events = app.Log.Diagnostics;
        events.Count.ShouldBe(2);
        events[0].Level.ShouldBe(LogLevel.Debug);
        events[0].Message.ShouldBe("Begin GET /orders/7");
        events[1].Level.ShouldBe(LogLevel.Debug);
        events[1].Message.ShouldMatch(@"^End GET /orders/7 -> 204 \(\d+\.\dms\)$");
    }

    [Fact]
    public async Task The_query_string_is_never_logged()
    {
        await using var app = await DiagnosticsApp.StartAsync(Responding(200));

        await app.Client.GetAsync("/callback?code=secret-code");

        app.Log.Diagnostics.Count.ShouldBe(2);
        app.Log.Everything.ShouldNotContain("secret-code");
    }

    [Fact]
    public async Task A_request_short_circuited_by_later_middleware_is_still_closed()
    {
        var reached = false;

        await using var app = await DiagnosticsApp.StartAsync(app =>
        {
            app.UseDiagnosticsMonitor();
            app.Use((HttpContext context, RequestDelegate _) =>
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return Task.CompletedTask;
            });
            app.Run(_ =>
            {
                reached = true;
                return Task.CompletedTask;
            });
        });

        var response = await app.Client.GetAsync("/private");

        response.StatusCode.ShouldBe(HttpStatusCode.Unauthorized);
        reached.ShouldBeFalse();
        app.Log.Diagnostics[^1].Message.ShouldStartWith("End GET /private -> 401 ");
    }

    [Fact]
    public async Task A_request_that_throws_is_still_closed_and_reported_as_a_500()
    {
        await using var app = await DiagnosticsApp.StartAsync(app =>
        {
            app.UseDiagnosticsMonitor();
            app.Run(_ => throw new InvalidOperationException("boom"));
        });

        await Should.ThrowAsync<InvalidOperationException>(() => app.Client.GetAsync("/broken"));

        var end = app.Log.Diagnostics[^1];
        end.Message.ShouldStartWith("End GET /broken -> 500 ");
        end.Exception.ShouldBeOfType<InvalidOperationException>().Message.ShouldBe("boom");
    }

    [Fact]
    public async Task An_exception_caught_by_a_handler_inside_the_bracket_is_still_on_the_end_line()
    {
        await using var app = await DiagnosticsApp.StartAsync(app =>
        {
            app.UseDiagnosticsMonitor();
            app.UseExceptionHandler(new ExceptionHandlerOptions
            {
                ExceptionHandler = context =>
                {
                    context.Response.StatusCode = StatusCodes.Status500InternalServerError;
                    return Task.CompletedTask;
                },
            });
            app.Run(_ => throw new InvalidOperationException("boom"));
        });

        var response = await app.Client.GetAsync("/broken");

        response.StatusCode.ShouldBe(HttpStatusCode.InternalServerError);

        var end = app.Log.Diagnostics[^1];
        end.Message.ShouldStartWith("End GET /broken -> 500 ");
        end.Exception.ShouldBeOfType<InvalidOperationException>().Message.ShouldBe("boom");
    }

    [Fact]
    public async Task A_request_that_does_not_fail_has_no_exception_on_its_end_line()
    {
        await using var app = await DiagnosticsApp.StartAsync(Responding(404));

        await app.Client.GetAsync("/missing");

        app.Log.Diagnostics[^1].Exception.ShouldBeNull();
    }

    [Fact]
    public async Task Nothing_is_logged_when_the_category_is_not_debug_enabled()
    {
        var log = new CapturingLoggerProvider { DiagnosticsLevel = LogLevel.Information };
        await using var app = await DiagnosticsApp.StartAsync(Responding(200), log: log);

        var response = await app.Client.GetAsync("/quiet");

        response.StatusCode.ShouldBe(HttpStatusCode.OK);
        log.Diagnostics.ShouldBeEmpty();
    }

    [Theory]
    [InlineData(200, LogLevel.Information)]
    [InlineData(404, LogLevel.Information)]
    [InlineData(401, LogLevel.Warning)]
    [InlineData(403, LogLevel.Warning)]
    [InlineData(429, LogLevel.Warning)]
    [InlineData(500, LogLevel.Error)]
    [InlineData(503, LogLevel.Error)]
    public async Task With_the_level_by_outcome_option_the_end_line_takes_its_level_from_the_status(int status, LogLevel expected)
    {
        var log = new CapturingLoggerProvider { DiagnosticsLevel = LogLevel.Information };
        await using var app = await DiagnosticsApp.StartAsync(Responding(status), options => options.EndLevelByOutcome = true, log);

        await app.Client.GetAsync("/outcome");

        // Begin stays at Debug, so at Information the End line is the only event.
        var end = log.Diagnostics.ShouldHaveSingleItem();
        end.Level.ShouldBe(expected);
        end.Message.ShouldStartWith($"End GET /outcome -> {status} ");
    }

    [Fact]
    public async Task Skip_leaves_a_request_out_entirely()
    {
        await using var app = await DiagnosticsApp.StartAsync(
            app =>
            {
                app.UseDiagnosticsMonitor();
                app.UseRequestLogging();
                app.Run(_ => Task.CompletedTask);
            },
            options => options.Skip = context => context.Request.Path.StartsWithSegments("/health"));

        await app.Client.GetAsync("/health");
        app.Log.Diagnostics.ShouldBeEmpty();

        await app.Client.GetAsync("/orders");
        app.Log.Diagnostics.ShouldNotBeEmpty();
    }

    [Fact]
    public async Task A_request_refused_before_enrichment_still_has_a_correlation_id()
    {
        await using var app = await DiagnosticsApp.StartAsync(app =>
        {
            app.UseDiagnosticsMonitor();
            app.Use((HttpContext context, RequestDelegate _) =>
            {
                context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                return Task.CompletedTask;
            });
            app.UseDiagnosticsEnrichment();
        });

        using var request = new HttpRequestMessage(HttpMethod.Get, "/private");
        request.Headers.Add("X-Correlation-Id", "caller-id-1");

        var response = await app.Client.SendAsync(request);

        response.Headers.GetValues("X-Correlation-Id").ShouldBe(["caller-id-1"]);
        app.Log.Diagnostics.ShouldAllBe(e => e.CorrelationId == "caller-id-1");
    }
}
