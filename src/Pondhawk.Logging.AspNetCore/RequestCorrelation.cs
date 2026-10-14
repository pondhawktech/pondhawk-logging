// Copyright (c) Pond Hawk Technologies Inc. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System.Globalization;
using Microsoft.AspNetCore.Http;
using Pondhawk.Context;

namespace Pondhawk.Logging.AspNetCore;

/// <summary>
/// Settles a request's correlation id once, whichever middleware gets to it first: the monitor, so a request
/// refused before authentication has one too, or enrichment when the monitor is not used.
/// </summary>
internal static class RequestCorrelation
{
    /// <summary>The longest correlation id accepted from outside.</summary>
    public const int MaxLength = 64;

    private static readonly object ItemKey = new();

    /// <summary>
    /// Returns the request's correlation id, settling it on first call: the inbound header when well formed,
    /// otherwise the ambient one, otherwise a fresh one. It is set on the ambient <see cref="IOperationContext"/> and, when asked for,
    /// written on the response.
    /// </summary>
    public static string Ensure(HttpContext context, DiagnosticsOptions options)
    {
        if (context.Items.TryGetValue(ItemKey, out var settled) && settled is string known)
            return known;

        var header = options.CorrelationHeader;
        var id = Inbound(context, header) ?? Earlier() ?? Ulid.NewUlid().ToString(null, CultureInfo.InvariantCulture);

        context.Items[ItemKey] = id;
        Ambient<IOperationContext>.Current.CorrelationId = id;

        if (options.EchoCorrelationId && !string.IsNullOrEmpty(header))
        {
            // At the start of the response rather than now: an exception handler clears the headers set so far.
            context.Response.OnStarting(static state =>
            {
                var (response, name, value) = ((HttpResponse, string, string))state;
                response.Headers[name] = value;
                return Task.CompletedTask;
            }, (context.Response, header, id));
        }

        return id;
    }

    /// <summary>
    /// Whether a correlation id that came from outside may be used: 1 to <see cref="MaxLength"/> ASCII letters,
    /// digits, <c>-</c>, <c>_</c> and <c>.</c>. A public server can be sent anything, and the value ends up on
    /// every event of the request.
    /// </summary>
    public static bool IsWellFormed(string? id)
    {
        if (string.IsNullOrEmpty(id) || id.Length > MaxLength)
            return false;

        foreach (var c in id)
        {
            if (!char.IsAsciiLetterOrDigit(c) && c is not ('-' or '_' or '.'))
                return false;
        }

        return true;
    }

    private static string? Inbound(HttpContext context, string? header)
    {
        if (string.IsNullOrEmpty(header) || !context.Request.Headers.TryGetValue(header, out var values) || values.Count == 0)
            return null;

        var id = values[0];
        return IsWellFormed(id) ? id : null;
    }

    // Whatever set it earlier is trusted no further than the header: it may have copied it from the request.
    private static string? Earlier()
    {
        var id = Ambient<IOperationContext>.Current.CorrelationId;
        return IsWellFormed(id) ? id : null;
    }
}
