// Copyright (c) Pond Hawk Technologies Inc. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using Microsoft.Extensions.DependencyInjection;

namespace Pondhawk.Logging.AspNetCore;

/// <summary>Registers the options the diagnostics middlewares read.</summary>
public static class DiagnosticsServiceCollectionExtensions
{
    /// <summary>
    /// Configures the <see cref="DiagnosticsOptions"/> shared by the three diagnostics middlewares. Optional:
    /// without it they run on the defaults.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="configure">Changes to the defaults.</param>
    /// <returns>The service collection for chaining.</returns>
    public static IServiceCollection AddPondhawkDiagnostics(this IServiceCollection services, Action<DiagnosticsOptions>? configure = null)
    {
        ArgumentNullException.ThrowIfNull(services);

        var options = services.AddOptions<DiagnosticsOptions>();

        if (configure is not null)
            options.Configure(configure);

        return services;
    }
}
