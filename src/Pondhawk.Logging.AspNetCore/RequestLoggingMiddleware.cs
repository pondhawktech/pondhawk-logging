// Copyright (c) Pond Hawk Technologies Inc. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Pondhawk.Logging.AspNetCore;

/// <summary>
/// When the <c>Pondhawk.Diagnostics.Http</c> category is Debug-enabled, logs the whole inbound request — method,
/// URL, query, the endpoint and user it resolved to, headers and a buffered, rewound body — as a JSON payload,
/// then the response the same way: status, elapsed time, headers and, when asked for, the body. A no-op
/// otherwise: nothing is read and nothing is buffered.
/// </summary>
/// <remarks>
/// Register this <em>deep</em>, after routing and authentication, accepting that anything short-circuited
/// earlier never reaches it; the early, whole-pipeline bracket is <see cref="DiagnosticsMonitorMiddleware"/>'s
/// job. What it shows is the request as sent, less the values <see cref="DiagnosticsOptions"/> names for
/// masking.
/// </remarks>
public sealed class RequestLoggingMiddleware
{
    private readonly RequestDelegate _next;
    private readonly ILogger _logger;
    private readonly DiagnosticsOptions _options;

    /// <summary>Creates the middleware.</summary>
    /// <param name="next">The next middleware.</param>
    /// <param name="loggerFactory">The logger factory.</param>
    /// <param name="options">The diagnostics options.</param>
    public RequestLoggingMiddleware(RequestDelegate next, ILoggerFactory loggerFactory, IOptions<DiagnosticsOptions> options)
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

        if (!_logger.IsEnabled(LogLevel.Debug) || _options.Skip?.Invoke(context) == true)
        {
            await _next(context).ConfigureAwait(false);
            return;
        }

        var method = context.Request.Method;
        var path = context.Request.Path.Value;
        var started = Stopwatch.GetTimestamp();

        var request = await HttpDescriber.DescribeRequestAsync(context.Request, _options, context.RequestAborted).ConfigureAwait(false);

        JsonPayload.Log(_logger, LogLevel.Debug, $"HTTP {method} {path}", request);

        var capture = _options.CaptureResponseBody && HttpDescriber.Cap(_options) > 0
            ? new ResponseCaptureStream(context.Response, HttpDescriber.Cap(_options))
            : null;

        try
        {
            try
            {
                await _next(context).ConfigureAwait(false);
            }
            catch when (capture is not null)
            {
                // Whoever handles this decides what the response is; nothing half-written is pushed out first.
                await capture.RestoreAsync(flush: false, CancellationToken.None).ConfigureAwait(false);
                throw;
            }

            if (capture is not null)
                await capture.RestoreAsync(flush: true, context.RequestAborted).ConfigureAwait(false);

            var response = HttpDescriber.DescribeResponse(context.Response, _options, capture, Stopwatch.GetElapsedTime(started).TotalMilliseconds);

            JsonPayload.Log(_logger, LogLevel.Debug, $"HTTP {method} {path} -> {context.Response.StatusCode}", response);
        }
        finally
        {
            if (capture is not null)
                await capture.DisposeAsync().ConfigureAwait(false);
        }
    }
}
