// Copyright (c) Pond Hawk Technologies Inc. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace Pondhawk.Logging.AspNetCore;

/// <summary>
/// Checks a diagnostic probe token Watch Server issued: <c>base64url(payload) "." base64url(signature)</c>, the
/// signature ECDSA P-256 / SHA-256 (IEEE P1363) over the payload's base64url text, the payload
/// <c>{"v":1,"i":id,"d":domain,"s":subject|null,"x":expiry}</c>. It never throws: anything wrong with a token is
/// a refusal, and the request goes on as if no probe had been offered.
/// </summary>
internal static class ProbeToken
{
    /// <summary>The longest header value looked at; anything longer is refused unread.</summary>
    public const int MaxLength = 1024;

    /// <summary>How far ahead a token may expire. Watch issues no longer ones, so a longer one is forged.</summary>
    public static readonly TimeSpan MaxLifetime = TimeSpan.FromHours(4);

    /// <summary>The allowance for the issuing and checking clocks disagreeing, either way.</summary>
    public static readonly TimeSpan ClockSkew = TimeSpan.FromMinutes(1);

    private const int SignatureLength = 64;

    /// <summary>Checks <paramref name="token"/> against <paramref name="key"/> at <paramref name="now"/>.</summary>
    public static Check Verify(string token, ProbeKey? key, DateTimeOffset now)
    {
        try
        {
            return VerifyCore(token, key, now);
        }
        catch
        {
            // A token is outside input: whatever it makes fail is a refusal, never the request's problem.
            return Check.Refused("malformed");
        }
    }

    private static Check VerifyCore(string token, ProbeKey? key, DateTimeOffset now)
    {
        if (token.Length > MaxLength)
            return Check.Refused("too long");

        var dot = token.IndexOf('.', StringComparison.Ordinal);
        if (dot <= 0 || dot == token.Length - 1 || token.IndexOf('.', dot + 1) >= 0)
            return Check.Refused("malformed");

        var encodedPayload = token[..dot];
        if (!TryDecode(token[(dot + 1)..], out var signature) || signature.Length != SignatureLength)
            return Check.Refused("malformed");

        if (key is null)
            return Check.Refused("no probe key from Watch yet");

        using (var ecdsa = ECDsa.Create())
        {
            try
            {
                ecdsa.ImportSubjectPublicKeyInfo(key.PublicKey.Span, out _);
            }
            catch (CryptographicException)
            {
                return Check.Refused("the probe key from Watch is unusable");
            }

            if (!ecdsa.VerifyData(Encoding.ASCII.GetBytes(encodedPayload), signature, HashAlgorithmName.SHA256))
                return Check.Refused("bad signature");
        }

        // Only now is the payload read: it is Watch's, not the caller's.
        if (!TryDecode(encodedPayload, out var payloadBytes))
            return Check.Refused("malformed");

        using var payload = JsonDocument.Parse(payloadBytes);
        var root = payload.RootElement;

        var id = root.TryGetProperty("i", out var i) && i.ValueKind == JsonValueKind.String ? i.GetString() : null;

        if (!root.TryGetProperty("v", out var v) || v.ValueKind != JsonValueKind.Number || !v.TryGetInt32(out var version) || version != 1)
            return Check.Refused("unknown version", id);

        if (!root.TryGetProperty("d", out var d) || d.ValueKind != JsonValueKind.String
            || !string.Equals(d.GetString(), key.Domain, StringComparison.OrdinalIgnoreCase))
            return Check.Refused("for another domain", id);

        if (!root.TryGetProperty("x", out var x) || x.ValueKind != JsonValueKind.Number || !x.TryGetInt64(out var seconds))
            return Check.Refused("malformed", id);

        var expires = DateTimeOffset.FromUnixTimeSeconds(seconds);
        if (expires < now - ClockSkew)
            return Check.Refused("expired", id);
        if (expires > now + MaxLifetime + ClockSkew)
            return Check.Refused("expiry too far ahead", id);

        var subject = root.TryGetProperty("s", out var s) && s.ValueKind == JsonValueKind.String ? s.GetString() : null;
        return Check.Accepted(id, string.IsNullOrEmpty(subject) ? null : subject);
    }

    // base64url without padding, as the token is written.
    private static bool TryDecode(string text, out byte[] bytes)
    {
        var padded = new StringBuilder(text.Length + 3).Append(text).Replace('-', '+').Replace('_', '/');
        while (padded.Length % 4 != 0)
            padded.Append('=');

        var buffer = new byte[padded.Length];
        if (Convert.TryFromBase64String(padded.ToString(), buffer, out var written))
        {
            bytes = buffer[..written];
            return true;
        }

        bytes = [];
        return false;
    }

    /// <summary>A domain's probe key from Watch: the domain a token must name, and the public key (SubjectPublicKeyInfo).</summary>
    internal sealed record ProbeKey(string Domain, ReadOnlyMemory<byte> PublicKey);

    /// <summary>The outcome: accepted (with the user it is bound to, if any) or refused, and why.</summary>
    internal sealed record Check(bool IsAccepted, string? Reason, string? TokenId, string? Subject)
    {
        public static Check Accepted(string? tokenId, string? subject) => new(true, null, tokenId, subject);

        public static Check Refused(string reason, string? tokenId = null) => new(false, reason, tokenId, null);
    }
}
