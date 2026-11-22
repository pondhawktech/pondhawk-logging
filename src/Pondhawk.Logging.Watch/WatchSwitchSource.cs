// Copyright (c) Pond Hawk Technologies Inc. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System.Drawing;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Threading;
using CommunityToolkit.Diagnostics;
using Microsoft.Extensions.Logging;

namespace Pondhawk.Logging.Watch;

/// <summary>
/// A SwitchSource that fetches switch configuration from a Watch Server.
/// </summary>
/// <remarks>
/// <para>
/// Polls GET /api/switches?domain={domain} to fetch switch configuration. When switches are fetched,
/// Update() is called which increments Version. A conditional poll asks Watch to hold it until the
/// switches change (<c>Prefer: wait</c>, see <see cref="LongPollWait"/>); a held answer is followed by the
/// next poll at once, anything else by the poll interval.
/// </para>
/// <para>
/// Thread-safety: All operations are thread-safe. Polling runs on a background task.
/// </para>
/// </remarks>
public class WatchSwitchSource : SwitchSource, IAsyncDisposable
{
    private readonly HttpClient _client;
    private readonly WatchDestination _destination;
    private readonly TimeSpan _pollInterval;
    private readonly CancellationTokenSource _cts = new();
    private readonly object _startLock = new();
    private readonly ManualResetEventSlim _ready = new(false);
    private Task? _pollTask;
    private bool _started;
    private int _lifecycleDisposed;

    // The ETag of the last switch set we applied. Sent back as If-None-Match so the server can answer
    // 304 Not Modified when nothing changed, letting us skip both the download and the rebuild. It belongs
    // to the destination binding it was fetched under, so a rebind discards it.
    private string? _lastETag;

    // The domain's diagnostic probe key, delivered with the switches. Kept through a failed poll and a 304,
    // dropped on a rebind: a key belongs to the domain it came from.
    private ProbeKey? _probeKey;
    private long _eTagBindingVersion;

    /// <summary>
    /// Gets or sets whether polling is enabled. Default is true.
    /// </summary>
    public bool PollingEnabled { get; set; } = true;

    /// <summary>
    /// Creates a switch source for a fixed domain, resolved against the client's base address.
    /// </summary>
    /// <param name="client">The HTTP client to use for requests.</param>
    /// <param name="domain">The domain name to fetch switches for.</param>
    /// <param name="pollInterval">
    /// The interval between polls when long polling is off or not supported, and the wait after a failed poll.
    /// Default is 5 seconds.
    /// </param>
    public WatchSwitchSource(HttpClient client, string domain, TimeSpan? pollInterval = null)
        : this(client, NewRelativeDestination(domain), pollInterval)
    {
    }

    /// <summary>
    /// Creates a switch source for <paramref name="destination"/>, re-read on every poll so a
    /// <see cref="WatchDestination.Rebind"/> moves switch polling to the new server and domain.
    /// </summary>
    /// <param name="client">The HTTP client to use for requests.</param>
    /// <param name="destination">The server and domain to fetch switches for.</param>
    /// <param name="pollInterval">
    /// The interval between polls when long polling is off or not supported, and the wait after a failed poll.
    /// Default is 5 seconds.
    /// </param>
    public WatchSwitchSource(HttpClient client, WatchDestination destination, TimeSpan? pollInterval = null)
    {
        Guard.IsNotNull(client);
        Guard.IsNotNull(destination);

        _client = client;
        _destination = destination;
        _eTagBindingVersion = destination.Current.Version;
        // Conditional polling (If-None-Match) makes an unchanged poll a tiny 304 with no rebuild, so a
        // short interval is cheap and gives near-real-time switch propagation.
        _pollInterval = pollInterval ?? TimeSpan.FromSeconds(5);
    }

    private static WatchDestination NewRelativeDestination(string domain)
    {
        Guard.IsNotNull(domain);
        return WatchDestination.Relative(domain);
    }

    /// <summary>
    /// Starts polling for switch updates.
    /// </summary>
    /// <remarks>
    /// This method is idempotent - calling it multiple times has no additional effect.
    /// Blocks until the initial switch fetch completes (or times out after 5 seconds)
    /// to ensure switches are available before the first log event.
    /// Subsequent updates run on a background poll loop.
    /// </remarks>
    public override void Start()
    {
        lock (_startLock)
        {
            if (_started)
                return;
            _started = true;
        }

        _pollTask = Task.Run(() => PollLoopAsync(_cts.Token));

        // Wait for the initial fetch to complete on the background thread.
        // Uses a WaitHandle to avoid sync-over-async bridging.
        _ready.Wait(TimeSpan.FromSeconds(5));
    }

    /// <summary>
    /// Stops polling.
    /// </summary>
    public override void Stop()
    {
        _cts.Cancel();
    }

    /// <summary>
    /// Fetches switches from the server and updates the configuration. Answered at once: the server is never
    /// asked to hold this request.
    /// </summary>
    public override async Task UpdateAsync(CancellationToken ct = default)
    {
        try
        {
            await PollAsync(_destination.Current, longPoll: false, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Silently ignore failures — will retry on next poll
        }
    }

    /// <summary>
    /// Gets or sets how long Watch may hold a conditional switch poll open waiting for the switches to change
    /// (<c>Prefer: wait</c>). Default 55 seconds, Watch's cap. <see cref="TimeSpan.Zero"/> turns long polling
    /// off, so every poll is answered at once and the next one follows the poll interval. Set it before
    /// <see cref="Start"/>.
    /// </summary>
    /// <remarks>
    /// A held poll is answered as soon as the switches change, so a change reaches the process at once rather
    /// than on the next interval. After a held answer the next poll is sent at once; an older Watch that does
    /// not hold polls is polled at the poll interval, as is any server after a failed poll. When the
    /// <see cref="HttpClient.Timeout"/> leaves too little room, the wait is lowered to fit, or dropped.
    /// </remarks>
    public TimeSpan LongPollWait { get; set; } = DefaultLongPollWait;

    internal static readonly TimeSpan DefaultLongPollWait = TimeSpan.FromSeconds(55);

    // How long past the requested wait a held poll is given before it counts as a failure: Watch may answer a
    // little late, and the response still has to arrive.
    internal static readonly TimeSpan HoldGrace = TimeSpan.FromSeconds(15);

    // What one poll came to, which decides when the next is sent.
    internal enum PollOutcome
    {
        /// <summary>Watch held the poll (<c>Preference-Applied</c>): poll again at once.</summary>
        Held,

        /// <summary>Answered without a hold — long polling off or not supported, or unbound: wait the interval.</summary>
        Answered,

        /// <summary>No answer, an error status, or an answer that could not be applied: wait the interval.</summary>
        Failed,

        /// <summary>The binding polled was replaced by a rebind: poll the new one at once.</summary>
        Rebound
    }

    internal async Task<PollOutcome> PollAsync(WatchDestination.Binding binding, bool longPoll, CancellationToken ct)
    {
        try
        {
            // An ETag names a switch set on the server we fetched it from. After a rebind it would have us
            // claim to hold the new domain's switches when we hold the old domain's, so drop it.
            if (_eTagBindingVersion != binding.Version)
            {
                _eTagBindingVersion = binding.Version;
                _lastETag = null;
                Volatile.Write(ref _probeKey, null);
            }

            // Nothing to poll until a server is named. The switch table keeps the defaults AddWatch gave it,
            // and the poll loop polls again as soon as a rebind names one.
            if (binding.IsUnbound)
                return PollOutcome.Answered;

            // Watch holds a poll only when it can tell whether anything changed, so only one with an ETag asks.
            var wait = longPoll && _lastETag is not null ? EffectiveWait(LongPollWait, _client.Timeout) : 0;

            using var request = NewRequest(binding, wait);

            // A rebind ends a held poll at once; a hold that runs well past the wait asked for is a failure.
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, binding.Replaced);
            if (wait > 0)
                linked.CancelAfter(TimeSpan.FromSeconds(wait) + HoldGrace);

            try
            {
                using var response = await _client.SendAsync(request, linked.Token).ConfigureAwait(false);

                var held = response.Headers.Contains("Preference-Applied");

                // Unchanged since our last successful fetch: skip both the download and the rebuild.
                if (response.StatusCode == HttpStatusCode.NotModified)
                    return held ? PollOutcome.Held : PollOutcome.Answered;

                if (!response.IsSuccessStatusCode || !await ApplyAsync(response, binding, linked.Token).ConfigureAwait(false))
                    return PollOutcome.Failed;

                return held ? PollOutcome.Held : PollOutcome.Answered;
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested && binding.Replaced.IsCancellationRequested)
            {
                return PollOutcome.Rebound;
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch
        {
            // Will retry after the poll interval.
            return PollOutcome.Failed;
        }
    }

    private HttpRequestMessage NewRequest(WatchDestination.Binding binding, int wait)
    {
        var request = new HttpRequestMessage(HttpMethod.Get, binding.SwitchesUri);
        if (_lastETag is not null)
            request.Headers.TryAddWithoutValidation("If-None-Match", _lastETag);
        if (wait > 0)
            request.Headers.TryAddWithoutValidation("Prefer", "wait=" + wait.ToString(CultureInfo.InvariantCulture));
        if (binding.ApiKey is not null)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", binding.ApiKey);
        return request;
    }

    // Installs the switches a 200 carries; false when it carries none.
    private async Task<bool> ApplyAsync(HttpResponseMessage response, WatchDestination.Binding binding, CancellationToken ct)
    {
        var payload = await response.Content.ReadFromJsonAsync<SwitchesResponse>(ct).ConfigureAwait(false);
        if (payload?.Switches is null)
            return false;

        var defs = payload.Switches.Select(s => new SwitchDef
        {
            Pattern = s.Pattern,
            Tag = s.Tag,
            Level = (LogLevel)Math.Clamp(s.Level, (int)LogLevel.Trace, (int)LogLevel.None),
            Color = Color.FromArgb(s.Color)
        }).ToList();

        Update(defs);
        Volatile.Write(ref _probeKey, ProbeKeyOf(payload.ProbeKey, binding.Domain));

        // Only remember the ETag once the corresponding switches are actually applied, so a
        // mid-parse failure doesn't leave us claiming to hold a set we never installed.
        _lastETag = response.Headers.ETag?.ToString();
        return true;
    }

    /// <summary>
    /// The whole seconds to ask Watch to hold a poll, or 0 to ask for no hold: the configured wait, lowered so
    /// the hold and its grace fit inside the client's own timeout.
    /// </summary>
    internal static int EffectiveWait(TimeSpan configured, TimeSpan clientTimeout)
    {
        var wait = configured;
        if (clientTimeout != Timeout.InfiniteTimeSpan && wait > clientTimeout - HoldGrace)
            wait = clientTimeout - HoldGrace;

        return wait >= TimeSpan.FromSeconds(1) ? (int)Math.Min(wait.TotalSeconds, int.MaxValue) : 0;
    }

    /// <summary>
    /// The domain's diagnostic probe key — its name and the public key (SubjectPublicKeyInfo) a probe token's
    /// signature is checked against — or null when Watch has sent none for the domain bound now. The shape is the
    /// one registered under <see cref="ProbeContract.KeyServiceKey"/>.
    /// </summary>
    internal (string Domain, ReadOnlyMemory<byte> PublicKey)? CurrentProbeKey =>
        Volatile.Read(ref _probeKey) is { } key ? (key.Domain, key.PublicKey) : null;

    // A key that is not base64 is no key: a probe token cannot be checked against it, so none is accepted.
    private static ProbeKey? ProbeKeyOf(string? encoded, string domain)
    {
        if (string.IsNullOrEmpty(encoded))
            return null;

        var bytes = new byte[encoded.Length];
        return Convert.TryFromBase64String(encoded, bytes, out var written)
            ? new ProbeKey(domain, bytes.AsMemory(0, written))
            : null;
    }

    private async Task PollLoopAsync(CancellationToken ct)
    {
        // Initial fetch — Start() blocks on _ready until this completes. It has no ETag, so it is never held.
        var outcome = PollOutcome.Failed;
        var polled = _destination.Current;
        try
        {
            outcome = await PollAsync(polled, longPoll: true, ct).ConfigureAwait(false);
        }
        catch
        {
            // Initial fetch failed — will retry in poll loop
        }
        finally
        {
            _ready.Set();
        }

        if (!PollingEnabled)
            return;

        while (!ct.IsCancellationRequested)
        {
            try
            {
                // After a held answer Watch is ready for the next poll at once; after anything else — an older
                // Watch, long polling off, a failure — wait the interval, so a proxy that cuts held requests
                // short never causes a tight loop. A rebind of the binding just polled ends the wait at once, even
                // one that came before the wait began: the new binding is polled now.
                if (outcome is not (PollOutcome.Held or PollOutcome.Rebound))
                    await DelayUnlessRebound(polled, ct).ConfigureAwait(false);

                polled = _destination.Current;
                outcome = await PollAsync(polled, longPoll: true, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch
            {
                // Continue polling even on failure
                outcome = PollOutcome.Failed;
            }
        }
    }

    private async Task DelayUnlessRebound(WatchDestination.Binding binding, CancellationToken ct)
    {
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, binding.Replaced);
        try
        {
            await Task.Delay(_pollInterval, linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            // Rebound: poll the new binding now.
        }
    }

    /// <summary>
    /// Disposes the switch source, cancelling polling, awaiting the background task, and releasing
    /// the cancellation source, ready handle, and switch lock.
    /// </summary>
    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _lifecycleDisposed, 1) == 0)
        {
            _cts.Cancel();

            if (_pollTask is not null)
            {
                try
                {
                    await _pollTask.ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    // Expected on shutdown
                }
            }

            _cts.Dispose();
            _ready.Dispose();
        }

        base.Dispose(true);
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// Releases the switch source's polling resources. Complements the base switch-lock disposal so
    /// the synchronous path fully cleans up (the async path additionally awaits the poll task).
    /// </summary>
    /// <param name="disposing">True if called from Dispose; false if from a finalizer.</param>
    protected override void Dispose(bool disposing)
    {
        if (disposing && Interlocked.Exchange(ref _lifecycleDisposed, 1) == 0)
        {
            _cts.Cancel();
            _cts.Dispose();
            _ready.Dispose();
        }

        base.Dispose(disposing);
    }

    // Held as one reference so a reader never sees a domain with another domain's key.
    private sealed record ProbeKey(string Domain, ReadOnlyMemory<byte> PublicKey);
}
