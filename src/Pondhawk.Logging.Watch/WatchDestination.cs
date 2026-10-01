// Copyright (c) Pond Hawk Technologies Inc. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using CommunityToolkit.Diagnostics;

namespace Pondhawk.Logging.Watch;

/// <summary>
/// The Watch URL the provider currently delivers to — server, domain, and optional API key — rebindable
/// while the process runs.
/// </summary>
/// <remarks>
/// <para>
/// Hold one of these, pass it to <c>AddWatch</c>, and call <see cref="Rebind"/> when the destination
/// changes — for an agent told where to log by configuration that only arrives after startup, this is the
/// supported way to follow it. It is also registered as a singleton by <c>AddWatch</c>, so it can be
/// resolved from DI rather than captured.
/// </para>
/// <para>
/// The Watch URL (<c>scheme://[key@]host[:port][/base-path]/&lt;domain&gt;</c>) is the provider's only
/// destination setting: the last path segment is the domain, and the optional user-info is a Watch API key,
/// sent as an <c>Authorization: Bearer</c> header and never in a request URI or in <see cref="Url"/>.
/// </para>
/// <para>
/// A rebind tears nothing down: the delivery channel, the critical-event buffer, and the HTTP client all
/// survive it, so events already buffered are not dropped. They are posted to the <em>new</em> destination
/// and carry the new domain — the batch's domain and the URL it is posted to always agree, because both
/// are read from one snapshot. The switch source picks up the new domain's switches on its next poll, and
/// the processor's circuit breaker is reset, so an unreachable old server does not hold the new one shut.
/// </para>
/// <para>
/// A destination can also start <see cref="Unbound"/>: a host whose destination arrives later — from a
/// mission plan, from user-data — has nothing to name at startup, and pointing it at a placeholder server
/// would make an open circuit breaker the normal state of a healthy process. While unbound the provider
/// posts nothing, counts no failures and leaves the circuit shut, holding Warning and above so the events
/// describing how the process came up are delivered once a destination is named.
/// </para>
/// <para>Thread-safe: readers take a consistent snapshot, and rebinds are serialized.</para>
/// </remarks>
public sealed class WatchDestination
{
    private readonly object _rebindLock = new();
    private Binding _current;

    /// <summary>Creates a destination bound to a Watch URL.</summary>
    /// <param name="watchUrl">
    /// The Watch URL, e.g. <c>http://localhost:11000/my-domain</c> or
    /// <c>https://pwk_id_secret@watch.example.com/my-domain</c>.
    /// </param>
    /// <exception cref="ArgumentException"><paramref name="watchUrl"/> is not a valid Watch URL.</exception>
    public WatchDestination(string watchUrl)
    {
        _current = Binding.Absolute(1, WatchUrl.Parse(watchUrl));
    }

    /// <summary>
    /// Creates a domain-only destination whose request URIs stay relative, resolved against the
    /// <see cref="HttpClient.BaseAddress"/> of whatever client is used. This is the shape behind the
    /// domain-only processor and switch-source constructors; it carries no API key and cannot be rebound
    /// to another server.
    /// </summary>
    /// <param name="domain">The domain name for log-event batches.</param>
    /// <returns>A relative destination.</returns>
    internal static WatchDestination Relative(string domain)
    {
        Guard.IsNotNull(domain);

        return new WatchDestination(Binding.Relative(1, domain));
    }

    /// <summary>
    /// Creates a destination that names no server yet. Nothing is posted until <see cref="Rebind"/> names
    /// one; events at Warning and above are held until then, and lower levels are discarded. Held events
    /// are delivered under the domain of the Watch URL that releases them.
    /// </summary>
    /// <returns>An unbound destination, ready to be passed to <c>AddWatch</c> and rebound later.</returns>
    public static WatchDestination Unbound()
        => new(Binding.Unbound(1));

    private WatchDestination(Binding binding) => _current = binding;

    /// <summary>
    /// Gets the current Watch URL with any API key masked (<c>***</c>) — safe to log or display — or an
    /// empty string when relative or unbound.
    /// </summary>
    public string Url => Current.Url;

    /// <summary>Gets the current domain name, or an empty string when unbound.</summary>
    public string Domain => Current.Domain;

    /// <summary>
    /// Gets the current binding's version. It starts at 1 and increments on every <see cref="Rebind"/>
    /// that changes something, which is how the switch source and the processor notice a rebind.
    /// </summary>
    public long Version => Current.Version;

    /// <summary>
    /// Gets whether a server has been named. <see langword="false"/> only for an <see cref="Unbound"/>
    /// destination that has not been rebound yet.
    /// </summary>
    public bool IsBound => !Current.IsUnbound;

    /// <summary>
    /// Points the provider at a different Watch URL — server, domain, or API key — effective from the next
    /// batch posted and the next switch poll. Rotating a key is a rebind to the same URL with the new key.
    /// </summary>
    /// <param name="watchUrl">The Watch URL to deliver to.</param>
    /// <returns>
    /// <see langword="true"/> when the destination changed; <see langword="false"/> when it already named
    /// this server, domain and key, in which case nothing is disturbed. Callers handed a configuration that
    /// repeats the same destination can therefore call this unconditionally.
    /// </returns>
    /// <exception cref="ArgumentException"><paramref name="watchUrl"/> is not a valid Watch URL.</exception>
    /// <exception cref="InvalidOperationException">The destination is relative and has no server URL to replace.</exception>
    public bool Rebind(string watchUrl)
    {
        var parsed = WatchUrl.Parse(watchUrl);

        lock (_rebindLock)
        {
            var current = _current;

            if (current.IsRelative)
            {
                throw new InvalidOperationException(
                    "This WatchDestination has no server URL of its own — its requests resolve against the HttpClient's BaseAddress — so it cannot be rebound. Construct it with a server URL, or use WatchDestination.Unbound, to make the destination rebindable.");
            }

            if (parsed.Equals(current.Source))
                return false;

            Volatile.Write(ref _current, Binding.Absolute(current.Version + 1, parsed));
            return true;
        }
    }

    /// <summary>Gets the current binding as one consistent snapshot of version, domain, and URIs.</summary>
    internal Binding Current => Volatile.Read(ref _current);

    /// <summary>
    /// One immutable view of the destination. Consumers read it once per batch or poll so the domain they
    /// stamp on a batch cannot disagree with the URL they post it to.
    /// </summary>
    internal sealed class Binding
    {
        private Binding(long version, WatchUrl? source, string domain, Uri? sinkUri, Uri? switchesUri, bool isRelative, bool isUnbound)
        {
            Version = version;
            Source = source;
            Domain = domain;
            SinkUri = sinkUri;
            SwitchesUri = switchesUri;
            IsRelative = isRelative;
            IsUnbound = isUnbound;
        }

        public long Version { get; }

        /// <summary>The Watch URL this binding was made from; <see langword="null"/> when relative or unbound.</summary>
        public WatchUrl? Source { get; }

        /// <summary>The redacted Watch URL, or an empty string when relative or unbound.</summary>
        public string Url => Source?.Redacted ?? string.Empty;

        /// <summary>The API key to send as <c>Authorization: Bearer</c>; <see langword="null"/> for none.</summary>
        public string? ApiKey => Source?.ApiKey;

        public string Domain { get; }

        /// <summary>The URI to post event batches to; <see langword="null"/> when unbound.</summary>
        public Uri? SinkUri { get; }

        /// <summary>The URI to poll this domain's switches from; <see langword="null"/> when unbound.</summary>
        public Uri? SwitchesUri { get; }

        /// <summary>True when the URIs are relative and resolve against the client's base address.</summary>
        public bool IsRelative { get; }

        /// <summary>True when no server has been named yet, so there is nowhere to post or poll.</summary>
        public bool IsUnbound { get; }

        public static Binding Absolute(long version, WatchUrl url)
            => new(
                version,
                url,
                url.Domain,
                new Uri(url.BaseUri, SinkPath),
                new Uri(url.BaseUri, SwitchesPath(url.Domain)),
                isRelative: false,
                isUnbound: false);

        public static Binding Unbound(long version)
            => new(version, source: null, string.Empty, null, null, isRelative: false, isUnbound: true);

        public static Binding Relative(long version, string domain)
            => new(
                version,
                source: null,
                domain,
                new Uri(SinkPath, UriKind.Relative),
                new Uri(SwitchesPath(domain), UriKind.Relative),
                isRelative: true,
                isUnbound: false);

        private const string SinkPath = "api/sink";

        private static string SwitchesPath(string domain)
            => "api/switches?domain=" + Uri.EscapeDataString(domain);
    }
}
