// Copyright (c) Pond Hawk Technologies Inc. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System.Collections.Concurrent;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;

namespace Pondhawk.Logging.AspNetCore;

/// <summary>
/// The diagnostic probe for one request: its token checked once, whichever of the monitor and enrichment gets
/// to it first. An unbound token probes the request at once; a token bound to a user waits for enrichment to
/// know who the caller is. A refused token leaves the request exactly as if none had been sent.
/// </summary>
internal static class RequestProbe
{
    // Refusals already warned about, so a client still sending a stale token does not log a Warning per request.
    private const int RememberedRefusals = 256;
    private static readonly ConcurrentDictionary<string, byte> Refused = new(StringComparer.Ordinal);

    private static readonly object ItemKey = new();

    /// <summary>Checks the request's probe token, once, and begins an unbound probe.</summary>
    public static void Offer(HttpContext context, DiagnosticsOptions options, ILogger logger)
    {
        if (context.Items.ContainsKey(ItemKey))
            return;

        context.Items[ItemKey] = null;

        if (!options.Probe || string.IsNullOrEmpty(options.ProbeHeader)
            || !context.Request.Headers.TryGetValue(options.ProbeHeader, out var values) || values.Count == 0)
            return;

        // No key source means no Watch provider to deliver a key: the probe is off, and nothing is refused.
        if (ProbeContract.KeySource(context) is not { } keys)
            return;

        var token = values[0] ?? string.Empty;
        var check = ProbeToken.Verify(token, keys() is { } key ? new ProbeToken.ProbeKey(key.Domain, key.PublicKey) : null, DateTimeOffset.UtcNow);

        if (!check.IsAccepted)
        {
            Refuse(logger, token, check.Reason!, check.TokenId);
            return;
        }

        if (check.Subject is null)
        {
            Begin(logger, check.TokenId, subject: null);
            return;
        }

        context.Items[ItemKey] = new Pending(token, check.TokenId, check.Subject);
    }

    /// <summary>Begins a probe bound to a user once the request's subject is known, if it is that user.</summary>
    public static void Bind(HttpContext context, ILogger logger, string? subjectName)
    {
        if (!context.Items.TryGetValue(ItemKey, out var item) || item is not Pending pending)
            return;

        context.Items[ItemKey] = null;

        if (string.Equals(pending.Subject, subjectName, StringComparison.OrdinalIgnoreCase))
            Begin(logger, pending.TokenId, pending.Subject);
        else
            Refuse(logger, pending.Token, "for another user", pending.TokenId);
    }

    private static void Begin(ILogger logger, string? tokenId, string? subject)
    {
        if (ProbeContract.Begin())
            DiagnosticsLog.ProbeBegun(logger, tokenId ?? "(none)", subject ?? "any user");
    }

    private static void Refuse(ILogger logger, string token, string reason, string? tokenId)
    {
        if (Refused.Count >= RememberedRefusals)
            Refused.Clear();

        var first = Refused.TryAdd(token, 0);
        DiagnosticsLog.ProbeRefused(logger, first ? LogLevel.Warning : LogLevel.Debug, reason, tokenId ?? "(unknown)");
    }

    private sealed record Pending(string Token, string? TokenId, string Subject);
}
