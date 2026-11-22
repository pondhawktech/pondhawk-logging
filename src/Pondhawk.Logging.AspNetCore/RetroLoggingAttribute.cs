// Copyright (c) Pond Hawk Technologies Inc. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

namespace Pondhawk.Logging.AspNetCore;

/// <summary>
/// Endpoint metadata that marks each request to the endpoint for retro logging: a provider that honours it (the
/// Watch provider does) keeps the Debug events its switch would drop, and sends them only if the request logs an
/// Error. Put it on a controller or an action, or use <c>WithRetroLogging()</c>.
/// </summary>
/// <remarks>
/// <see cref="DiagnosticsEnrichmentMiddleware"/> sets the mark, so events logged before it — the monitor's "Begin"
/// line, routing, authentication — are not kept.
/// </remarks>
[AttributeUsage(AttributeTargets.Class | AttributeTargets.Method, Inherited = true, AllowMultiple = false)]
public sealed class RetroLoggingAttribute : Attribute;
