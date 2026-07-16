/*
The MIT License (MIT)

Copyright (c) 2024 Pond Hawk Technologies Inc.

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
*/

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
