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

    /// <summary>
    /// Who the current unit of work is for — a user name or id — or null. Sinks stamp it on every event logged
    /// within the work (Watch's Subject).
    /// </summary>
    public static string? Subject => Find(LogPropertyNames.SubjectKey);

    /// <summary>The tenant the current unit of work belongs to, or null. Sinks stamp it on every event logged within the work.</summary>
    public static string? Tenant => Find(LogPropertyNames.TenantKey);

    /// <summary>
    /// Sets who the current unit of work is for: after <see cref="Begin()"/> for background work, or in middleware
    /// from the signed-in user. It covers everything logged within the current <see cref="Activity"/>, child
    /// activities included. Unlike the correlation id it is not baggage, so it is never sent to the services this
    /// one calls. Null falls back to an enclosing scope's subject; with no current Activity there is nothing to
    /// attach it to, and it is ignored.
    /// </summary>
    /// <param name="subject">The subject, such as a user name or id.</param>
    public static void SetSubject(string? subject) => Activity.Current?.SetCustomProperty(LogPropertyNames.SubjectKey, subject);

    /// <summary>
    /// Sets the tenant the current unit of work belongs to, as <see cref="SetSubject"/> does the subject: it covers
    /// the current <see cref="Activity"/> and its children, stays in the process, and is ignored with no Activity.
    /// </summary>
    /// <param name="tenant">The tenant's name or id.</param>
    public static void SetTenant(string? tenant) => Activity.Current?.SetCustomProperty(LogPropertyNames.TenantKey, tenant);

    // Custom properties are not inherited the way baggage is, so the nearest activity that set one wins.
    private static string? Find(string key)
    {
        for (var activity = Activity.Current; activity is not null; activity = activity.Parent)
        {
            if (activity.GetCustomProperty(key) is string value)
                return value;
        }

        return null;
    }

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
