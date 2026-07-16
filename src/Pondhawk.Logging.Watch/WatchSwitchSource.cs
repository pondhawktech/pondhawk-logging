/*
The MIT License (MIT)

Copyright (c) 2024 Pond Hawk Technologies Inc.

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
*/

using System.Drawing;
using System.Net.Http.Json;
using System.Threading;
using CommunityToolkit.Diagnostics;
using Serilog.Events;

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
    private readonly Lock _startLock = new();
    private readonly ManualResetEventSlim _ready = new(false);
    private Task? _pollTask;
    private bool _started;
    private int _lifecycleDisposed;

    /// <summary>
    /// Gets or sets whether polling is enabled. Default is true.
    /// </summary>
    public bool PollingEnabled { get; set; } = true;

    /// <summary>
    /// Creates a new WatchSwitchSource.
    /// </summary>
    /// <param name="client">The HTTP client to use for requests.</param>
    /// <param name="domain">The domain name to fetch switches for.</param>
    /// <param name="pollInterval">The interval between polls. Default is 30 seconds.</param>
    public WatchSwitchSource(HttpClient client, string domain, TimeSpan? pollInterval = null)
    {
        Guard.IsNotNull(client);
        Guard.IsNotNull(domain);

        _client = client;
        _domain = domain;
        _pollInterval = pollInterval ?? TimeSpan.FromSeconds(30);
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
            var response = await _client.GetFromJsonAsync<SwitchesResponse>(url, ct).ConfigureAwait(false);

            if (response?.Switches is not null)
            {
                var defs = response.Switches.Select(s => new SwitchDef
                {
                    Pattern = s.Pattern,
                    Tag = s.Tag,
                    Level = s.Level > (int)LogEventLevel.Fatal ? LogEventLevel.Fatal : (LogEventLevel)s.Level,
                    Color = Color.FromArgb(s.Color)
                }).ToList();

                Update(defs);
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
