// Copyright (c) Pond Hawk Technologies Inc. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

namespace Pondhawk.Logging;

/// <summary>
/// Where the key that verifies a diagnostic probe token comes from. A provider that delivers to Watch Server
/// registers one (the Watch provider gets the key with its switches); without one, no probe is accepted.
/// </summary>
public interface IDiagnosticProbeKey
{
    /// <summary>The key for the domain currently delivered to, or null when there is none yet.</summary>
    DiagnosticProbeKey? Current { get; }
}
