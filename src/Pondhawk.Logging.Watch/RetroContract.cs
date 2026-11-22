// Copyright (c) Pond Hawk Technologies Inc. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System.Diagnostics;

namespace Pondhawk.Logging.Watch;

/// <summary>
/// The retro-logging mark as this provider reads it: a custom property on the unit of work's
/// <see cref="Activity"/>. A copy rather than a reference, because no package may reference Pondhawk.Logging; the
/// test project checks it against the API's <c>RetroLogging</c>.
/// </summary>
internal static class RetroContract
{
    /// <summary>The tag a kept event is given when it is sent.</summary>
    public const string Tag = "Retro";

    /// <summary>The <see cref="Activity"/> custom property that marks a unit of work for retro logging.</summary>
    public const string PropertyName = "Pondhawk.RetroLogging";

    /// <summary>Whether the current unit of work is marked: this activity or one it was started under.</summary>
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
}
