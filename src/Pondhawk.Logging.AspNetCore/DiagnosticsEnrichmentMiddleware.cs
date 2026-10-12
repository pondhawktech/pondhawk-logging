// Copyright (c) Pond Hawk Technologies Inc. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Options;

namespace Pondhawk.Logging.AspNetCore;

/// <summary>
/// Puts who a request is for on every event logged within it: the subject from the authenticated principal and
/// the tenant from the application's delegate, through <see cref="CorrelationManager"/>. It also settles the
/// correlation id when <see cref="DiagnosticsMonitorMiddleware"/> has not already.
/// </summary>
/// <remarks>
/// Run this after authentication, so <c>context.User</c> is the caller. All three values attach to the
/// request's <see cref="System.Diagnostics.Activity"/>; with none current there is nothing to attach them to and
/// the request goes on without them.
/// </remarks>
public sealed class DiagnosticsEnrichmentMiddleware
{
    private readonly RequestDelegate _next;
    private readonly DiagnosticsOptions _options;

    /// <summary>Creates the middleware.</summary>
    /// <param name="next">The next middleware.</param>
    /// <param name="options">The diagnostics options.</param>
    public DiagnosticsEnrichmentMiddleware(RequestDelegate next, IOptions<DiagnosticsOptions> options)
    {
        ArgumentNullException.ThrowIfNull(next);
        ArgumentNullException.ThrowIfNull(options);

        _next = next;
        _options = options.Value;
    }

    /// <summary>Invokes the middleware.</summary>
    /// <param name="context">The HTTP context.</param>
    /// <returns>A task that completes when the request has been handled.</returns>
    public Task Invoke(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        RequestCorrelation.Ensure(context, _options);

        var subject = Subject(context);
        if (!string.IsNullOrEmpty(subject))
            CorrelationManager.SetSubject(subject);

        var tenant = _options.Tenant?.Invoke(context);
        if (!string.IsNullOrEmpty(tenant))
            CorrelationManager.SetTenant(tenant);

        return _next(context);
    }

    // The subject an event carries is there to be read, so with the default it is the caller's username, or the
    // client id when there is no username, ahead of the sub claim the default gives. A delegate the application
    // set is used as it is.
    private string? Subject(HttpContext context)
    {
        if (_options.Subject != DiagnosticsOptions.DefaultSubject)
            return _options.Subject?.Invoke(context);

        return NullIfEmpty(context.User.FindFirst("preferred_username")?.Value)
            ?? NullIfEmpty(context.User.FindFirst("client_id")?.Value)
            ?? DiagnosticsOptions.DefaultSubject(context);
    }

    private static string? NullIfEmpty(string? value) => string.IsNullOrEmpty(value) ? null : value;
}
