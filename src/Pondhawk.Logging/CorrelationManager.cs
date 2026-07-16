// Copyright (c) Pond Hawk Technologies Inc. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

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
