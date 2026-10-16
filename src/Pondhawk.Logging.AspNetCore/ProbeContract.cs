// Copyright (c) Pond Hawk Technologies Inc. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System.Diagnostics;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;

namespace Pondhawk.Logging.AspNetCore;

/// <summary>
/// The diagnostic probe as these middlewares see it. A probe is a custom property on the request's
/// <see cref="Activity"/>, which a provider that honours probes reads (the Watch provider does); the key a token
/// is checked against comes from that provider as a keyed service of built-in types. A copy of the contract
/// rather than a reference, because no package may reference Pondhawk.Logging; the test project checks it against
/// the API's <c>DiagnosticProbe</c> and the Watch provider's registration.
/// </summary>
internal static class ProbeContract
{
    /// <summary>The <see cref="Activity"/> custom property that marks a probed unit of work.</summary>
    public const string PropertyName = "Pondhawk.DiagnosticProbe";

    /// <summary>
    /// The service key the probe key is registered under, as a
    /// <c>Func&lt;(string Domain, ReadOnlyMemory&lt;byte&gt; PublicKey)?&gt;</c> that returns the current key or null.
    /// </summary>
    public const string KeyServiceKey = "Pondhawk.DiagnosticProbeKey";

    private static readonly object Marker = new();

    /// <summary>Probes the current unit of work until its <see cref="Activity"/> ends.</summary>
    /// <returns>False when there is no current <see cref="Activity"/> to mark.</returns>
    public static bool Begin()
    {
        var activity = Activity.Current;
        if (activity is null)
            return false;

        activity.SetCustomProperty(PropertyName, Marker);
        return true;
    }

    /// <summary>
    /// The request's source of probe keys, or null when no provider registered one — or the container cannot
    /// hold keyed services, which is treated the same: no key, so no token is accepted.
    /// </summary>
    public static Func<(string Domain, ReadOnlyMemory<byte> PublicKey)?>? KeySource(HttpContext context) =>
        context.RequestServices is IKeyedServiceProvider keyed
            ? keyed.GetKeyedService(typeof(Func<(string Domain, ReadOnlyMemory<byte> PublicKey)?>), KeyServiceKey)
                as Func<(string Domain, ReadOnlyMemory<byte> PublicKey)?>
            : null;
}
