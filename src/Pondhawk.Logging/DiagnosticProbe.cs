// Copyright (c) Pond Hawk Technologies Inc. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System.Diagnostics;

namespace Pondhawk.Logging;

/// <summary>
/// Marks the current unit of work — an HTTP request, in practice — as probed: support has asked, with a token
/// Watch Server issued, for everything it logs at Trace whatever the switch table says. A provider that
/// honours the probe (the Watch provider does) reads <see cref="IsActive"/> when it decides what to keep.
/// </summary>
/// <remarks>
/// The mark is a custom property on the current <see cref="Activity"/>, so it covers that activity and every
/// activity started under it in this process, and ends with it. It is not baggage and not a tag, so it is never
/// sent on an outbound call: a probe on one service does not probe the services it calls.
/// </remarks>
public static class DiagnosticProbe
{
    /// <summary>The tag a provider gives the events of a probed unit of work.</summary>
    public const string Tag = "Diagnostics";

    private const string PropertyName = "Pondhawk.DiagnosticProbe";

    private static readonly object Marker = new();

    /// <summary>Whether the current unit of work is probed.</summary>
    public static bool IsActive
    {
        get
        {
            for (var activity = Activity.Current; activity is not null; activity = activity.Parent)
            {
                if (activity.GetCustomProperty(PropertyName) is not null)
                    return true;
            }

            return false;
        }
    }

    /// <summary>
    /// Probes the current unit of work: <see cref="IsActive"/> is true from here until the current
    /// <see cref="Activity"/> ends.
    /// </summary>
    /// <returns>False when there is no current <see cref="Activity"/> to mark.</returns>
    public static bool Begin()
    {
        var activity = Activity.Current;
        if (activity is null)
            return false;

        activity.SetCustomProperty(PropertyName, Marker);
        return true;
    }
}
