// Copyright (c) Pond Hawk Technologies Inc. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

namespace Pondhawk.Logging.Watch;

/// <summary>
/// A parsed Watch URL — the single string that tells a process where to log:
/// <c>scheme://[key@]host[:port][/base-path]/&lt;domain&gt;</c>.
/// </summary>
/// <remarks>
/// <para>
/// The last path segment is the domain. Everything before it is the base the Watch API lives under
/// (<c>&lt;base&gt;/api/sink</c>, <c>&lt;base&gt;/api/switches</c>). The optional user-info is a Watch
/// API key: it is never sent in the URL, only as an <c>Authorization: Bearer</c> header, and never
/// appears in <see cref="Redacted"/>.
/// </para>
/// <para>
/// A key is refused over plain <c>http</c> unless the host is loopback, so a misconfigured URL fails at
/// startup rather than sending a credential in the clear.
/// </para>
/// </remarks>
internal sealed class WatchUrl : IEquatable<WatchUrl>
{
    private WatchUrl(Uri baseUri, string domain, string? apiKey, string redacted)
    {
        BaseUri = baseUri;
        Domain = domain;
        ApiKey = apiKey;
        Redacted = redacted;
    }

    /// <summary>Gets the absolute base the Watch API paths resolve against, always ending in <c>/</c>.</summary>
    public Uri BaseUri { get; }

    /// <summary>Gets the domain log events are delivered under (the URL's last path segment, decoded).</summary>
    public string Domain { get; }

    /// <summary>Gets the API key from the URL's user-info, or <see langword="null"/> when there is none.</summary>
    public string? ApiKey { get; }

    /// <summary>Gets the URL with any key masked (<c>***</c>), safe to log or display.</summary>
    public string Redacted { get; }

    /// <summary>Parses a Watch URL.</summary>
    /// <param name="url">The Watch URL, e.g. <c>https://pwk_id_secret@watch.example.com/orders-service</c>.</param>
    /// <returns>The parsed URL.</returns>
    /// <exception cref="ArgumentException">The URL is not a valid Watch URL; the message says why, without the key.</exception>
    public static WatchUrl Parse(string url)
    {
        if (string.IsNullOrWhiteSpace(url))
            throw new ArgumentException("A Watch URL is required, e.g. http://localhost:11000/my-domain.", nameof(url));

        if (!Uri.TryCreate(url.Trim(), UriKind.Absolute, out var uri) ||
            (!string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.Ordinal) && !string.Equals(uri.Scheme, Uri.UriSchemeHttps, StringComparison.Ordinal)))
        {
            throw new ArgumentException("A Watch URL must be an absolute http or https URL, e.g. http://localhost:11000/my-domain.", nameof(url));
        }

        // Describe the URL without its key in every error from here on.
        var display = Display(uri, hasKey: uri.UserInfo.Length > 0);

        if (uri.Query.Length > 0 || uri.Fragment.Length > 0)
            throw new ArgumentException($"A Watch URL has no query string or fragment: {display}", nameof(url));

        string? apiKey = null;
        if (uri.UserInfo.Length > 0)
        {
            if (uri.UserInfo.Contains(':', StringComparison.Ordinal))
                throw new ArgumentException($"A Watch URL's user-info is a single API key, not a user name and password: {display}", nameof(url));

            apiKey = Uri.UnescapeDataString(uri.UserInfo);

            if (string.Equals(uri.Scheme, Uri.UriSchemeHttp, StringComparison.Ordinal) && !uri.IsLoopback)
                throw new ArgumentException($"A Watch URL carrying an API key must use https (plain http is allowed only for loopback): {display}", nameof(url));
        }

        var path = uri.AbsolutePath.TrimEnd('/');
        var lastSlash = path.LastIndexOf('/');
        var domain = Uri.UnescapeDataString(path[(lastSlash + 1)..]);

        if (string.IsNullOrWhiteSpace(domain))
            throw new ArgumentException($"A Watch URL must end with the domain to log to, e.g. http://localhost:11000/my-domain: {display}", nameof(url));

        var baseUri = new UriBuilder(uri.Scheme, uri.Host, uri.Port, path[..(lastSlash + 1)]).Uri;

        return new WatchUrl(baseUri, domain, apiKey, display);
    }

    private static string Display(Uri uri, bool hasKey)
    {
        var builder = new UriBuilder(uri) { UserName = hasKey ? "***" : string.Empty, Password = string.Empty };
        return builder.Uri.GetComponents(UriComponents.AbsoluteUri, UriFormat.UriEscaped);
    }

    /// <inheritdoc />
    public bool Equals(WatchUrl? other) =>
        other is not null &&
        BaseUri.Equals(other.BaseUri) &&
        string.Equals(Domain, other.Domain, StringComparison.Ordinal) &&
        string.Equals(ApiKey, other.ApiKey, StringComparison.Ordinal);

    /// <inheritdoc />
    public override bool Equals(object? obj) => Equals(obj as WatchUrl);

    /// <inheritdoc />
    public override int GetHashCode() => HashCode.Combine(BaseUri, StringComparer.Ordinal.GetHashCode(Domain));

    /// <summary>Returns the redacted URL.</summary>
    /// <returns><see cref="Redacted"/>.</returns>
    public override string ToString() => Redacted;
}
