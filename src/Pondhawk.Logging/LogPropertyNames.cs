// Copyright (c) Pond Hawk Technologies Inc. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

namespace Pondhawk.Logging;

/// <summary>
/// Well-known Serilog property names written by the logging API (<see cref="SerilogExtensions"/>,
/// <see cref="MethodLogger"/>) and read by log sinks. Public because these constants form a contract
/// shared across packages (e.g. the Watch sink reads them), not just an internal detail.
/// </summary>
public static class LogPropertyNames
{
    /// <summary>The prefix shared by all Pondhawk logging control properties.</summary>
    public const string Prefix = "Pondhawk.";

    /// <summary>Method-tracing nesting delta: +1 on entry, -1 on exit.</summary>
    public const string Nesting = "Pondhawk.Nesting";

    /// <summary>The <c>PayloadType</c> enum value (as int) describing the attached payload.</summary>
    public const string PayloadType = "Pondhawk.PayloadType";

    /// <summary>The serialized payload content string.</summary>
    public const string PayloadContent = "Pondhawk.PayloadContent";

    /// <summary>The correlation identifier property attached to emitted events.</summary>
    public const string CorrelationId = "Pondhawk.CorrelationId";

    /// <summary>The <see cref="System.Diagnostics.Activity"/> baggage key used to flow the correlation id.</summary>
    public const string CorrelationBaggageKey = "pondhawk.correlation";
}
