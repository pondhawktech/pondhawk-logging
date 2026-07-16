// Copyright (c) Pond Hawk Technologies Inc. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

namespace Pondhawk.Logging;

/// <summary>
/// Serializes objects to a string format for log event payloads.
/// </summary>
public interface IObjectSerializer
{
    /// <summary>
    /// Serializes an object to a string.
    /// </summary>
    /// <param name="source">The object to serialize.</param>
    /// <returns>A tuple containing the payload type and serialized string.</returns>
    (PayloadType Type, string Payload) Serialize(object? source);
}
