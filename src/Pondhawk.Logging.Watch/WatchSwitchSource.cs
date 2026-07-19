// Copyright (c) Pond Hawk Technologies Inc. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System.Drawing;
using System.Net;
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
/// Periodically polls GET /api/switches?domain={domain} to fetch switch configuration.
/// When switches are fetched, Update() is called which increments Version.
/// </para>
/// <para>
/// Thread-safety: All operations are thread-safe. Polling runs on a background task.
/// </para>
/// </remarks>
public class WatchSwitchSource : SwitchSource, IAsyncDisposable
{
    private readonly HttpClient _client;
    private readonly string _domain;
    private readonly TimeSpan _pollInterval;
    private readonly CancellationTokenSource _cts = new();
    private readonly object _startLock = new();
    private readonly ManualResetEventSlim _ready = new(false);
    private Task? _pollTask;
    private bool _started;
    private int _lifecycleDisposed;

    // The ETag of the last switch set we applied. Sent back as If-None-Match so the server can answer
    // 304 Not Modified when nothing changed, letting us skip both the download and the rebuild.
    private string? _lastETag;

    /// <summary>
    /// Gets or sets whether polling is enabled. Default is true.
    /// </summary>
    public bool PollingEnabled { get; set; } = true;

    /// <summary>
    /// Creates a new WatchSwitchSource.
    /// </summary>
    /// <param name="client">The HTTP client to use for requests.</param>
    /// <param name="domain">The domain name to fetch switches for.</param>
    /// <param name="pollInterval">The interval between polls. Default is 5 seconds.</param>
    public WatchSwitchSource(HttpClient client, string domain, TimeSpan? pollInterval = null)
    {
        Guard.IsNotNull(client);
        Guard.IsNotNull(domain);

        _client = client;
        _domain = domain;
        // Conditional polling (If-None-Match) makes an unchanged poll a tiny 304 with no rebuild, so a
        // short interval is cheap and gives near-real-time switch propagation.
        _pollInterval = pollInterval ?? TimeSpan.FromSeconds(5);
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
    /// Fetches switches from the server and updates the configuration.
    /// </summary>
    public override async Task UpdateAsync(CancellationToken ct = default)
    {
        try
        {
            var url = $"api/switches?domain={Uri.EscapeDataString(_domain)}";
            using var request = new HttpRequestMessage(HttpMethod.Get, url);
            if (_lastETag is not null)
                request.Headers.TryAddWithoutValidation("If-None-Match", _lastETag);

            using var response = await _client.SendAsync(request, ct).ConfigureAwait(false);

            // Unchanged since our last successful fetch: skip both the download and the rebuild.
            if (response.StatusCode == HttpStatusCode.NotModified)
                return;

            if (!response.IsSuccessStatusCode)
                return;

            var payload = await response.Content.ReadFromJsonAsync<SwitchesResponse>(ct).ConfigureAwait(false);
            if (payload?.Switches is not null)
            {
                var defs = payload.Switches.Select(s => new SwitchDef
                {
                    Pattern = s.Pattern,
                    Tag = s.Tag,
                    Level = s.Level > (int)LogLevel.Critical ? LogLevel.Critical : (LogLevel)s.Level,
                    Color = Color.FromArgb(s.Color)
                }).ToList();

                Update(defs);

                // Only remember the ETag once the corresponding switches are actually applied, so a
                // mid-parse failure doesn't leave us claiming to hold a set we never installed.
                _lastETag = response.Headers.ETag?.ToString();
            }
        }
        catch
        {
            // Silently ignore failures — will retry on next poll
        }
    }

    private async Task PollLoopAsync(CancellationToken ct)
    {
        // Initial fetch — Start() blocks on _ready until this completes.
        try
        {
            await UpdateAsync(ct).ConfigureAwait(false);
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
                await Task.Delay(_pollInterval, ct).ConfigureAwait(false);
                await UpdateAsync(ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch
            {
                // Continue polling even on failure
            }
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
}
