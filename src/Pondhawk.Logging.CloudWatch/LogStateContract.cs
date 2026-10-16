// Copyright (c) Pond Hawk Technologies Inc. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

namespace Pondhawk.Logging.CloudWatch;

/// <summary>
/// What the Pondhawk.Logging API puts on the log state, as this provider reads it: the property names and the
/// payload type numbers. A copy rather than a reference, because no package may reference Pondhawk.Logging — an
/// application references it from NuGet, vendors its source, or drops it. The test project checks these against the API's own values.
/// </summary>
internal static class LogStateContract
{
    /// <summary>The method-tracing delta (<c>LogPropertyNames.Nesting</c>).</summary>
    public const string Nesting = "Pondhawk.Nesting";

    /// <summary>The payload's type, as an <c>int</c> (<c>LogPropertyNames.PayloadType</c>).</summary>
    public const string PayloadType = "Pondhawk.PayloadType";

    /// <summary>The payload's text (<c>LogPropertyNames.PayloadContent</c>).</summary>
    public const string PayloadContent = "Pondhawk.PayloadContent";

    /// <summary>A context object, already serialized to JSON (<c>LogPropertyNames.ErrorContext</c>).</summary>
    public const string ErrorContext = "Pondhawk.ErrorContext";

    /// <summary><c>PayloadType.Json</c>.</summary>
    public const int JsonPayload = 1;

    /// <summary><c>PayloadType.Text</c>.</summary>
    public const int TextPayload = 4;
}
