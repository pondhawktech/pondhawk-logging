// Copyright (c) Pond Hawk Technologies Inc. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System.Security.Claims;
using Microsoft.AspNetCore.Http;

namespace Pondhawk.Logging.AspNetCore;

/// <summary>
/// What the three diagnostics middlewares do, read by all of them. Register changes with
/// <see cref="DiagnosticsServiceCollectionExtensions.AddPondhawkDiagnostics"/>; without it the defaults apply.
/// </summary>
/// <remarks>
/// The request dump shows the whole request by default — it is a diagnostics tool, and it only runs when an
/// application has added it and someone has switched its category to Debug. Masking is by name and the
/// application's choice: <see cref="MaskedHeaders"/>, <see cref="MaskedQueryParameters"/> and
/// <see cref="MaskedFormFields"/>.
/// </remarks>
public sealed class DiagnosticsOptions
{
    /// <summary>The default for <see cref="CorrelationHeader"/>.</summary>
    public const string DefaultCorrelationHeader = "X-Correlation-Id";

    /// <summary>The default for <see cref="MaxBodyBytes"/>.</summary>
    public const int DefaultMaxBodyBytes = 64 * 1024;

    /// <summary>The most <see cref="MaxBodyBytes"/> can be; a larger value is treated as this.</summary>
    public const int LargestMaxBodyBytes = 1024 * 1024;

    /// <summary>
    /// The header an inbound correlation id is read from and, with <see cref="EchoCorrelationId"/>, written back
    /// on. Empty reads and writes none.
    /// </summary>
    public string CorrelationHeader { get; set; } = DefaultCorrelationHeader;

    /// <summary>Whether the request's correlation id is written on the response, so a caller can quote it.</summary>
    public bool EchoCorrelationId { get; set; } = true;

    /// <summary>
    /// Who the request is for. The default is the <c>sub</c> claim, then the name identifier claim. Null, or a
    /// null or empty result, sets no subject.
    /// </summary>
    public Func<HttpContext, string?>? Subject { get; set; } = DefaultSubject;

    /// <summary>The tenant the request belongs to. None by default.</summary>
    public Func<HttpContext, string?>? Tenant { get; set; }

    /// <summary>
    /// True leaves a request out of the monitor and the request dump — health checks, static files. It still
    /// gets its correlation id, subject and tenant.
    /// </summary>
    public Func<HttpContext, bool>? Skip { get; set; }

    /// <summary>
    /// Headers whose values the request dump replaces by their length. <c>Authorization</c> and <c>Cookie</c> by
    /// default; add to it or clear it. Names are matched without regard to case.
    /// </summary>
    public ISet<string> MaskedHeaders { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "Authorization", "Cookie" };

    /// <summary>Query parameters whose values the request dump replaces by their length. Empty by default.</summary>
    public ISet<string> MaskedQueryParameters { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Fields of an <c>application/x-www-form-urlencoded</c> body whose values the request dump replaces by their
    /// length — <c>password</c>, <c>client_secret</c>. Empty by default. It does not reach into JSON bodies.
    /// </summary>
    public ISet<string> MaskedFormFields { get; } = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Whether the request dump reads and writes a text body. False describes every body by content type and
    /// length without reading it.
    /// </summary>
    public bool CaptureBody { get; set; } = true;

    /// <summary>
    /// The most of a body the request dump writes; the rest is noted as cut. Zero or less describes the body
    /// without reading it, and anything over <see cref="LargestMaxBodyBytes"/> is treated as that.
    /// </summary>
    public int MaxBodyBytes { get; set; } = DefaultMaxBodyBytes;

    /// <summary>
    /// Off by default, so the monitor's "End" line is at Debug like everything else here. On, it is logged at
    /// Information, at Warning for 401, 403 and 429, and at Error for 5xx — which makes it visible at a
    /// provider's default level.
    /// </summary>
    public bool EndLevelByOutcome { get; set; }

    /// <summary>The default <see cref="Subject"/>: the <c>sub</c> claim, then the name identifier claim.</summary>
    /// <param name="context">The HTTP context.</param>
    /// <returns>The subject, or null when the principal carries neither claim.</returns>
    public static string? DefaultSubject(HttpContext context)
    {
        ArgumentNullException.ThrowIfNull(context);

        return context.User.FindFirst("sub")?.Value ?? context.User.FindFirst(ClaimTypes.NameIdentifier)?.Value;
    }
}
