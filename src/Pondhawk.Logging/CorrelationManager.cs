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

using System.Diagnostics;

namespace Pondhawk.Logging;

/// <summary>
/// Provides correlation context for logging operations.
/// </summary>
public static class CorrelationManager
{
    /// <summary>
    /// The baggage key used to store the Watch correlation ID.
    /// </summary>
    public const string BaggageKey = LogPropertyNames.CorrelationBaggageKey;

    /// <summary>
    /// Begins a new correlation scope with a fresh Ulid.
    /// Use this at the start of background work (message processing, timer callbacks, etc.)
    /// </summary>
    /// <returns>An IDisposable that ends the correlation scope when disposed.</returns>
    public static IDisposable Begin()
    {
        return Begin(Ulid.NewUlid().ToString(null, System.Globalization.CultureInfo.InvariantCulture));
    }

    /// <summary>
    /// Begins a new correlation scope with the specified correlation ID.
    /// </summary>
    /// <param name="correlationId">The correlation ID to use.</param>
    /// <returns>An IDisposable that ends the correlation scope when disposed.</returns>
    public static IDisposable Begin(string correlationId)
    {
        var activity = new Activity("CorrelationManager");
        activity.SetBaggage(BaggageKey, correlationId);
        activity.Start();
        return new CorrelationScope(activity);
    }

    /// <summary>
    /// Sets the correlation ID on the current Activity.
    /// Use this in middleware when an Activity already exists.
    /// </summary>
    /// <param name="correlationId">The correlation ID to set. If null, generates a new Ulid.</param>
    public static void Set(string? correlationId = null)
    {
        var id = correlationId ?? Ulid.NewUlid().ToString(null, System.Globalization.CultureInfo.InvariantCulture);
        Activity.Current?.SetBaggage(BaggageKey, id);
    }

    /// <summary>
    /// Gets the current correlation ID from Activity baggage.
    /// </summary>
    /// <returns>The correlation ID, or null if not set.</returns>
    public static string? Current => Activity.Current?.GetBaggageItem(BaggageKey);

    private sealed class CorrelationScope : IDisposable
    {
        private readonly Activity _activity;
        private bool _disposed;

        public CorrelationScope(Activity activity)
        {
            _activity = activity;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _activity.Stop();
            _activity.Dispose();
        }
    }
}
