// Copyright (c) Pond Hawk Technologies Inc. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using CommunityToolkit.Diagnostics;

namespace Pondhawk.Logging.Watch;

/// <summary>
/// The Watch server and domain the provider currently delivers to, rebindable while the process runs.
/// </summary>
/// <remarks>
/// <para>
/// Hold one of these, pass it to <c>AddWatch</c>, and call <see cref="Rebind"/> when the destination
/// changes — for an agent told where to log by configuration that only arrives after startup, this is the
/// supported way to follow it. It is also registered as a singleton by <c>AddWatch</c>, so it can be
/// resolved from DI rather than captured.
/// </para>
/// <para>
/// A rebind tears nothing down: the delivery channel, the critical-event buffer, and the HTTP client all
/// survive it, so events already buffered are not dropped. They are posted to the <em>new</em> destination
/// and carry the new domain — the batch's domain and the URL it is posted to always agree, because both
/// are read from one snapshot. The switch source picks up the new domain's switches on its next poll, and
/// the processor's circuit breaker is reset, so an unreachable old server does not hold the new one shut.
/// </para>
/// <para>Thread-safe: readers take a consistent snapshot, and rebinds are serialized.</para>
/// </remarks>
public sealed class WatchDestination
{
    private readonly object _rebindLock = new();
    private Binding _current;

    /// <summary>Creates a destination bound to a Watch server URL and domain.</summary>
    /// <param name="serverUrl">The Watch server URL, e.g. <c>http://localhost:11000</c>.</param>
    /// <param name="domain">The domain name for log-event batches (typically the application's name).</param>
    public WatchDestination(string serverUrl, string domain)
    {
        Guard.IsNotNullOrWhiteSpace(serverUrl);
        Guard.IsNotNullOrWhiteSpace(domain);

        _current = Binding.Absolute(1, serverUrl, domain);
    }

    /// <summary>
    /// Creates a domain-only destination whose request URIs stay relative, resolved against the
    /// <see cref="HttpClient.BaseAddress"/> of whatever client is used. This is the shape behind the
    /// server-url-less constructors kept for compatibility; it cannot be rebound to another server.
    /// </summary>
    /// <param name="domain">The domain name for log-event batches.</param>
    internal WatchDestination(string domain)
    {
        Guard.IsNotNull(domain);

        _current = Binding.Relative(1, domain);
    }

    /// <summary>Gets the current Watch server URL, or an empty string for a relative destination.</summary>
    public string ServerUrl => Current.ServerUrl;

    /// <summary>Gets the current domain name.</summary>
    public string Domain => Current.Domain;

    /// <summary>
    /// Gets the current binding's version. It starts at 1 and increments on every <see cref="Rebind"/>
    /// that changes something, which is how the switch source and the processor notice a rebind.
    /// </summary>
    public long Version => Current.Version;

    /// <summary>
    /// Points the provider at a different Watch server and domain, effective from the next batch posted
    /// and the next switch poll.
    /// </summary>
    /// <param name="serverUrl">The Watch server URL to deliver to.</param>
    /// <param name="domain">The domain name for log-event batches.</param>
    /// <returns>
    /// <see langword="true"/> when the destination changed; <see langword="false"/> when it already named
    /// this server and domain, in which case nothing is disturbed. Callers handed a configuration that
    /// repeats the same destination can therefore call this unconditionally.
    /// </returns>
    /// <exception cref="InvalidOperationException">The destination is relative and has no server URL to replace.</exception>
    public bool Rebind(string serverUrl, string domain)
    {
        Guard.IsNotNullOrWhiteSpace(serverUrl);
        Guard.IsNotNullOrWhiteSpace(domain);

        lock (_rebindLock)
        {
            var current = _current;

            if (current.IsRelative)
            {
                throw new InvalidOperationException(
                    "This WatchDestination has no server URL of its own — its requests resolve against the HttpClient's BaseAddress — so it cannot be rebound. Construct it with a server URL to make the destination rebindable.");
            }

            if (string.Equals(current.ServerUrl, serverUrl, StringComparison.Ordinal) &&
                string.Equals(current.Domain, domain, StringComparison.Ordinal))
            {
                return false;
            }

            Volatile.Write(ref _current, Binding.Absolute(current.Version + 1, serverUrl, domain));
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
        private Binding(long version, string serverUrl, string domain, Uri sinkUri, Uri switchesUri, bool isRelative)
        {
            Version = version;
            ServerUrl = serverUrl;
            Domain = domain;
            SinkUri = sinkUri;
            SwitchesUri = switchesUri;
            IsRelative = isRelative;
        }

        public long Version { get; }

        public string ServerUrl { get; }

        public string Domain { get; }

        /// <summary>The URI to post event batches to.</summary>
        public Uri SinkUri { get; }

        /// <summary>The URI to poll this domain's switches from.</summary>
        public Uri SwitchesUri { get; }

        /// <summary>True when the URIs are relative and resolve against the client's base address.</summary>
        public bool IsRelative { get; }

        public static Binding Absolute(long version, string serverUrl, string domain)
        {
            var root = new Uri(serverUrl.TrimEnd('/') + "/", UriKind.Absolute);

            return new Binding(
                version,
                serverUrl,
                domain,
                new Uri(root, SinkPath),
                new Uri(root, SwitchesPath(domain)),
                isRelative: false);
        }

        public static Binding Relative(long version, string domain)
            => new(
                version,
                string.Empty,
                domain,
                new Uri(SinkPath, UriKind.Relative),
                new Uri(SwitchesPath(domain), UriKind.Relative),
                isRelative: true);

        private const string SinkPath = "api/sink";

        private static string SwitchesPath(string domain)
            => "api/switches?domain=" + Uri.EscapeDataString(domain);
    }
}
