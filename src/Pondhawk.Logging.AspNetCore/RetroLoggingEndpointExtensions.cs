// Copyright (c) Pond Hawk Technologies Inc. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using Microsoft.AspNetCore.Builder;

namespace Pondhawk.Logging.AspNetCore;

/// <summary>Marks endpoints for retro logging.</summary>
public static class RetroLoggingEndpointExtensions
{
    /// <summary>
    /// Marks each request to the endpoints for retro logging (<see cref="RetroLoggingAttribute"/>).
    /// </summary>
    /// <typeparam name="TBuilder">The endpoint convention builder.</typeparam>
    /// <param name="builder">The endpoints.</param>
    /// <returns>The builder for chaining.</returns>
    public static TBuilder WithRetroLogging<TBuilder>(this TBuilder builder)
        where TBuilder : IEndpointConventionBuilder
    {
        ArgumentNullException.ThrowIfNull(builder);
        return builder.WithMetadata(new RetroLoggingAttribute());
    }
}
