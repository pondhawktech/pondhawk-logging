// Copyright (c) Pond Hawk Technologies Inc. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System.Diagnostics;

namespace Pondhawk.Logging;

/// <summary>
/// Retro logging for a unit of work: a provider that honours it (the Watch provider does) keeps the Debug events
/// its switch would drop, and sends them only if the unit of work logs an Error — so a failure arrives with the
/// trail that led to it, and a success costs no extra events.
/// </summary>
/// <remarks>
/// A marked unit of work pays for each Debug call it makes: the call site formats the event even when the trail is
/// discarded. Trace is never kept, so <c>LogObject</c> at its default level serializes nothing. Payload content is
/// dropped from a kept event; its title stays. An ASP.NET Core endpoint opts in with <c>WithRetroLogging()</c> or
/// <c>[RetroLogging]</c> (Pondhawk.Logging.AspNetCore); other work, such as a background job, calls
/// <see cref="Begin"/> inside its own <see cref="Activity"/>.
/// </remarks>
public static class RetroLogging
{
    /// <summary>The tag a provider gives the events it kept and sent after an error.</summary>
    public const string Tag = "Retro";

    private const string PropertyName = "Pondhawk.RetroLogging";

    private static readonly object Marker = new();

    /// <summary>Whether the current unit of work is marked for retro logging.</summary>
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
    /// Marks the current unit of work for retro logging: <see cref="IsActive"/> is true from here until the current
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
