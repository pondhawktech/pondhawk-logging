// Copyright (c) Pond Hawk Technologies Inc. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System.Diagnostics;

namespace Pondhawk.Logging.AspNetCore;

/// <summary>
/// The retro-logging mark as these middlewares set it: a custom property on the request's <see cref="Activity"/>,
/// which a provider that honours it reads (the Watch provider does). A copy of the contract rather than a reference,
/// because no package may reference Pondhawk.Logging; the test project checks it against the API's
/// <c>RetroLogging</c>.
/// </summary>
internal static class RetroContract
{
    /// <summary>The <see cref="Activity"/> custom property that marks a unit of work for retro logging.</summary>
    public const string PropertyName = "Pondhawk.RetroLogging";

    private static readonly object Marker = new();

    /// <summary>Marks the current unit of work for retro logging until its <see cref="Activity"/> ends.</summary>
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
