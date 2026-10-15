// Copyright (c) Pond Hawk Technologies Inc. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Pondhawk.Context;

namespace Pondhawk.Logging.AspNetCore;

/// <summary>
/// Puts who a request is for on every event logged within it: the subject from the authenticated principal and
/// the tenant from the application's delegate, in the ambient <see cref="IOperationContext"/>. It also settles the
/// correlation id when <see cref="DiagnosticsMonitorMiddleware"/> has not already.
/// </summary>
/// <remarks>
/// Run this after authentication, so <c>context.User</c> is the caller. A request nobody is known to have made
/// is given <see cref="Operation.Anonymous"/> as its subject, so a log or an audit trail never shows a blank; code
/// that learns later who is acting, as a sign-in page does, sets the subject itself. All of it attaches to the
/// request's <see cref="System.Diagnostics.Activity"/>; with none current there is nothing to attach it to and the
/// request goes on without it.
/// </remarks>
public sealed class DiagnosticsEnrichmentMiddleware
{
    private readonly RequestDelegate _next;
    private readonly DiagnosticsOptions _options;
    private readonly ILogger _logger;

    /// <summary>Creates the middleware.</summary>
    /// <param name="next">The next middleware.</param>
    /// <param name="loggerFactory">The logger factory.</param>
    /// <param name="options">The diagnostics options.</param>
    public DiagnosticsEnrichmentMiddleware(RequestDelegate next, ILoggerFactory loggerFactory, IOptions<DiagnosticsOptions> options)
    {
        ArgumentNullException.ThrowIfNull(next);
        ArgumentNullException.ThrowIfNull(loggerFactory);
        ArgumentNullException.ThrowIfNull(options);

        _next = next;
        _logger = loggerFactory.CreateLogger(DiagnosticsLog.Category);
        _options = options.Value;
    }

    /// <summary>Invokes the middleware.</summary>
    /// <param name="context">The HTTP context.</param>
    /// <returns>A task that completes when the request has been handled.</returns>
    public Task Invoke(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        RequestCorrelation.Ensure(context, _options);
        RequestProbe.Offer(context, _options, _logger);

        var operation = Ambient<IOperationContext>.Current;

        var id = SubjectId(context);
        var name = NullIfEmpty(SubjectName(context));
        operation.SubjectId = id ?? name ?? Operation.Anonymous;
        operation.SubjectName = name ?? id ?? Operation.Anonymous;

        var tenant = _options.Tenant?.Invoke(context);
        if (!string.IsNullOrEmpty(tenant))
            operation.Tenant = tenant;

        // A probe bound to a user starts here, now that the caller is known.
        RequestProbe.Bind(context, _logger, operation.SubjectName);

        return _next(context);
    }

    // Who the caller is by what does not change: the sub claim (or name identifier), or the client id when a
    // machine is acting for itself.
    private static string? SubjectId(HttpContext context) =>
        NullIfEmpty(DiagnosticsOptions.DefaultSubject(context)) ?? NullIfEmpty(context.User.FindFirst("client_id")?.Value);

    // The subject an event carries is there to be read, so with the default it is the caller's username, or the
    // client id when there is no username, ahead of the sub claim the default gives. A delegate the application
    // set is used as it is.
    private string? SubjectName(HttpContext context)
    {
        if (_options.Subject != DiagnosticsOptions.DefaultSubject)
            return _options.Subject?.Invoke(context);

        return NullIfEmpty(context.User.FindFirst("preferred_username")?.Value)
            ?? NullIfEmpty(context.User.FindFirst("client_id")?.Value)
            ?? DiagnosticsOptions.DefaultSubject(context);
    }

    private static string? NullIfEmpty(string? value) => string.IsNullOrEmpty(value) ? null : value;
}
