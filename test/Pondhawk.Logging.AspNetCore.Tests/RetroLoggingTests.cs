// Copyright (c) Pond Hawk Technologies Inc. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Pondhawk.Logging.AspNetCore.Tests.Support;
using Shouldly;
using Xunit;

namespace Pondhawk.Logging.AspNetCore.Tests;

/// <summary>An endpoint opts into retro logging; enrichment marks each request to it.</summary>
public class RetroLoggingTests
{
    private static string Marked() => RetroLogging.IsActive ? "marked" : "not marked";

    private static Task<DiagnosticsApp> Start() => DiagnosticsApp.StartAsync(app =>
    {
        app.UseRouting();
        app.UseDiagnosticsEnrichment();
        app.UseEndpoints(endpoints =>
        {
            endpoints.MapGet("/fluent", Marked).WithRetroLogging();
            endpoints.MapGet("/attribute", [RetroLogging] () => Marked());
            endpoints.MapGet("/plain", Marked);
        });
        app.Run(context => context.Response.WriteAsync(Marked()));
    });

    [Theory]
    [InlineData("/fluent", "marked")]
    [InlineData("/attribute", "marked")]
    [InlineData("/plain", "not marked")]
    [InlineData("/no-such-endpoint", "not marked")]
    public async Task Only_a_request_to_an_endpoint_that_opted_in_is_marked(string path, string expected)
    {
        await using var app = await Start();

        (await app.Client.GetStringAsync(path)).ShouldBe(expected);
    }
}
