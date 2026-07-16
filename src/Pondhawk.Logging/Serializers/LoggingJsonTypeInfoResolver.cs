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

using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization.Metadata;

namespace Pondhawk.Logging.Serializers;

/// <summary>
/// Custom JSON type info resolver that provides safe property access and sensitive data handling.
/// </summary>
/// <remarks>
/// <para>
/// This resolver wraps all property getters to:
/// <list type="bullet">
/// <item>Catch exceptions thrown by property getters (e.g., MemoryStream.ReadTimeout)</item>
/// <item>Mask sensitive data marked with <see cref="SensitiveAttribute"/></item>
/// </list>
/// </para>
/// <para>
/// System.Text.Json does not provide a built-in way to handle exceptions during
/// property access. Without this resolver, a single throwing property causes the
/// entire serialization to fail. This resolver ensures logging continues even
/// when some properties are inaccessible.
/// </para>
/// </remarks>
internal sealed class LoggingJsonTypeInfoResolver : DefaultJsonTypeInfoResolver
{
    public override JsonTypeInfo GetTypeInfo(Type type, JsonSerializerOptions options)
    {
        var typeInfo = base.GetTypeInfo(type, options);

        if (typeInfo.Kind != JsonTypeInfoKind.Object)
            return typeInfo;

        for (var i = 0; i < typeInfo.Properties.Count; i++)
        {
            var prop = typeInfo.Properties[i];

            var sensitive = prop.AttributeProvider switch
            {
                MemberInfo mi => mi.GetCustomAttribute<SensitiveAttribute>(inherit: true),
                ParameterInfo pi => pi.GetCustomAttribute<SensitiveAttribute>(inherit: true),
                _ => null
            };

            var originalGetter = prop.Get;

            if (sensitive is not null)
            {
                // Replace the property with a string-typed one carrying the mask. The original
                // getter's return value is only inspected for presence, so the declared type no
                // longer matters — this avoids handing a string to, say, an int property's
                // converter, which would throw during writing and collapse the entire payload.
                var masked = typeInfo.CreateJsonPropertyInfo(typeof(string), prop.Name);
                masked.Get = o => MaskSensitive(o, originalGetter);
                typeInfo.Properties.RemoveAt(i);
                typeInfo.Properties.Insert(i, masked);
            }
            else
            {
                var propertyType = prop.PropertyType;
                prop.Get = o => SafePropertyGetter(o, originalGetter, propertyType);
            }
        }

        return typeInfo;
    }

    /// <summary>
    /// Safely gets a property value, returning a default if an exception occurs.
    /// </summary>
    private static object? SafePropertyGetter(object source, Func<object, object?>? getter, Type type)
    {
        object? GetDefault()
        {
            return type.IsValueType ? Activator.CreateInstance(type) : null;
        }

        try
        {
            if (getter is null)
                return GetDefault();

            return getter(source);
        }
        catch
        {
            return GetDefault();
        }
    }

    /// <summary>
    /// Produces the masked string for a sensitive property: "Sensitive - HasValue: true/false".
    /// Always returns a string (the property is re-typed as string), so it serializes cleanly
    /// regardless of the original property type. A throwing getter is reported as no value.
    /// </summary>
    private static string MaskSensitive(object source, Func<object, object?>? getter)
    {
        try
        {
            var value = getter is null ? null : getter(source);

            if (value is string s)
            {
                return $"Sensitive - HasValue: {!string.IsNullOrWhiteSpace(s)}";
            }

            return $"Sensitive - HasValue: {value is not null}";
        }
        catch
        {
            return "Sensitive - HasValue: false";
        }
    }
}
