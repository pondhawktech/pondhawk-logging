// Copyright (c) Pond Hawk Technologies Inc. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System.Diagnostics;
using Microsoft.AspNetCore.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Pondhawk.Logging.AspNetCore;

/// <summary>
/// Brackets each request with "Begin" and "End" — status and elapsed time — under the
/// <c>Pondhawk.Diagnostics.Http</c> category, and settles the request's correlation id.
/// </summary>
/// <remarks>
/// Register this <em>early</em>: it is the outer bracket, so it times the whole pipeline and still closes a
/// request that later middleware short-circuits (a 401 from authentication) or that throws — with the exception
/// on the End line, whether it came this far or an exception handler caught it first. It logs the path
/// only, never the query string. <see cref="RequestLoggingMiddleware"/> is registered deep for the detail of
/// the request, at the cost of missing what was refused earlier — the two are complementary by position.
/// </remarks>
public sealed class DiagnosticsMonitorMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger _logger;
    private readonly DiagnosticsOptions _options;

    /// <summary>Creates the middleware.</summary>
    /// <param name="next">The next middleware.</param>
    /// <param name="loggerFactory">The logger factory.</param>
    /// <param name="options">The diagnostics options.</param>
    public DiagnosticsMonitorMiddleware(RequestDelegate next, ILoggerFactory loggerFactory, IOptions<DiagnosticsOptions> options)
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
    public async Task Invoke(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        RequestCorrelation.Ensure(context, _options);

        if (_options.Skip?.Invoke(context) == true)
        {
            await _next(context).ConfigureAwait(false);
            return;
        }

        var method = context.Request.Method;
        var path = context.Request.Path.Value;
        var started = Stopwatch.GetTimestamp();
        Exception? thrown = null;

        DiagnosticsLog.Begin(_logger, method, path);

        try
        {
            await _next(context).ConfigureAwait(false);
        }
        catch (Exception e)
        {
            thrown = e;
            throw;
        }
        finally
        {
            // An exception that got this far leaves the status untouched; what the client is sent is a 500.
            var status = thrown is not null && !context.Response.HasStarted ? StatusCodes.Status500InternalServerError : context.Response.StatusCode;

            // The failure rides on the line that closes the request. Usually an exception handler inside this
            // bracket has already caught it and answered, and left it on the context.
            var exception = thrown ?? context.Features.Get<IExceptionHandlerFeature>()?.Error;

            var level = EndLevel(status);
            var elapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds;

            DiagnosticsLog.End(_logger, level, exception, method, path, status, elapsed);
        }
    }

    private LogLevel EndLevel(int status)
    {
        if (!_options.EndLevelByOutcome)
            return LogLevel.Debug;

        return status switch
        {
            >= 500 => LogLevel.Error,
            StatusCodes.Status401Unauthorized or StatusCodes.Status403Forbidden or StatusCodes.Status429TooManyRequests => LogLevel.Warning,
            _ => LogLevel.Information,
        };
    }
}
