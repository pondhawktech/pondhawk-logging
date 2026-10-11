// Copyright (c) Pond Hawk Technologies Inc. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using Microsoft.AspNetCore.Builder;

namespace Pondhawk.Logging.AspNetCore;

/// <summary>Adds the diagnostics middlewares to the pipeline.</summary>
public static class DiagnosticsApplicationBuilderExtensions
{
    /// <summary>
    /// Brackets each request with "Begin" and "End" (status, elapsed time) and settles its correlation id.
    /// Register it early, so it covers requests refused later.
    /// </summary>
    /// <param name="app">The application builder.</param>
    /// <returns>The application builder for chaining.</returns>
    public static IApplicationBuilder UseDiagnosticsMonitor(this IApplicationBuilder app) =>
        app.UseMiddleware<DiagnosticsMonitorMiddleware>();

    /// <summary>
    /// Puts the correlation id, subject and tenant on every event of the request. Register it after
    /// authentication.
    /// </summary>
    /// <param name="app">The application builder.</param>
    /// <returns>The application builder for chaining.</returns>
    public static IApplicationBuilder UseDiagnosticsEnrichment(this IApplicationBuilder app) =>
        app.UseMiddleware<DiagnosticsEnrichmentMiddleware>();

    /// <summary>
    /// Logs the whole inbound request when the <c>Pondhawk.Diagnostics.Http</c> category is Debug-enabled.
    /// Register it deep, after routing and authentication.
    /// </summary>
    /// <param name="app">The application builder.</param>
    /// <returns>The application builder for chaining.</returns>
    public static IApplicationBuilder UseRequestLogging(this IApplicationBuilder app) =>
        app.UseMiddleware<RequestLoggingMiddleware>();
}
