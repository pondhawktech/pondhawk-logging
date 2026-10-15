// Copyright (c) Pond Hawk Technologies Inc. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

namespace Pondhawk.Logging;

/// <summary>A Watch domain's probe public key: what a probe token for that domain is verified against.</summary>
public sealed class DiagnosticProbeKey
{
    /// <summary>Creates the key.</summary>
    /// <param name="domain">The Watch domain the key belongs to.</param>
    /// <param name="publicKey">The ECDSA P-256 public key, as SubjectPublicKeyInfo.</param>
    public DiagnosticProbeKey(string domain, ReadOnlyMemory<byte> publicKey)
    {
        ArgumentNullException.ThrowIfNull(domain);

        Domain = domain;
        PublicKey = publicKey;
    }

    /// <summary>The Watch domain the key belongs to; a token must name the same one.</summary>
    public string Domain { get; }

    /// <summary>The ECDSA P-256 public key, as SubjectPublicKeyInfo.</summary>
    public ReadOnlyMemory<byte> PublicKey { get; }
}
