// Copyright (c) Pond Hawk Technologies Inc. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System.Text.Json;
using System.Text.Json.Serialization;
using Pondhawk.Logging.Serializers;

namespace Pondhawk.Logging;

/// <summary>
/// Serializes objects to JSON format with safe property access and sensitive data handling.
/// </summary>
/// <remarks>
/// <para>
/// Uses System.Text.Json with settings optimized for logging:
/// <list type="bullet">
/// <item>WriteIndented for readability</item>
/// <item>ReferenceHandler.IgnoreCycles for circular references</item>
/// <item>LoggingJsonTypeInfoResolver for safe property access and [Sensitive] handling</item>
/// <item>Custom converters for Type and Attribute</item>
/// </list>
/// </para>
/// <para>
/// The custom resolver wraps all property getters to catch exceptions. This is
/// essential because some objects (e.g., MemoryStream) have properties that throw
/// when accessed, and System.Text.Json provides no built-in way to handle this.
/// </para>
/// <para>
/// Thread-safe - can be shared across threads.
/// </para>
/// </remarks>
public class JsonObjectSerializer : IObjectSerializer
{
    /// <summary>
    /// Singleton instance with default options.
    /// </summary>
    public static readonly JsonObjectSerializer Instance = new();

    private static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.General)
    {
        WriteIndented = true,
        ReferenceHandler = ReferenceHandler.IgnoreCycles,
        TypeInfoResolver = new LoggingJsonTypeInfoResolver(),
        Converters =
        {
            new TypeJsonConverter(),
            new AttributeJsonConverter()
        }
    };

    /// <summary>
    /// Serializes an object to JSON.
    /// </summary>
    /// <param name="source">The object to serialize.</param>
    /// <returns>The payload type (Json) and serialized string.</returns>
    public (PayloadType Type, string Payload) Serialize(object? source)
    {
        try
        {
            var json = JsonSerializer.Serialize(source, Options);
            return (PayloadType.Json, json);
        }
        catch (Exception)
        {
            // If serialization still fails, return empty object
            return (PayloadType.Json, "{}");
        }
    }
}
