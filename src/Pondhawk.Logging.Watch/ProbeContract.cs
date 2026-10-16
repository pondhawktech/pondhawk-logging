// Copyright (c) Pond Hawk Technologies Inc. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System.Diagnostics;

namespace Pondhawk.Logging.Watch;

/// <summary>
/// The diagnostic probe as this provider sees it. The mark is a custom property on the current
/// <see cref="Activity"/> (set by <c>Pondhawk.Logging.DiagnosticProbe.Begin</c> or by the ASP.NET Core
/// diagnostics), and the probe key goes to whoever checks probe tokens as a keyed service. A copy of the
/// contract rather than a reference, because no package may reference Pondhawk.Logging; the test project checks
/// it against the API's <c>DiagnosticProbe</c>.
/// </summary>
internal static class ProbeContract
{
    /// <summary>The tag a probed unit of work's events are given.</summary>
    public const string Tag = "Diagnostics";

    /// <summary>The <see cref="Activity"/> custom property that marks a probed unit of work.</summary>
    public const string PropertyName = "Pondhawk.DiagnosticProbe";

    /// <summary>
    /// The service key the probe key is registered under, as a
    /// <c>Func&lt;(string Domain, ReadOnlyMemory&lt;byte&gt; PublicKey)?&gt;</c> that returns the current key or null.
    /// </summary>
    public const string KeyServiceKey = "Pondhawk.DiagnosticProbeKey";

    /// <summary>Whether the current unit of work is probed: this activity or one it was started under is marked.</summary>
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
