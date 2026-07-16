// Copyright (c) Pond Hawk Technologies Inc. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System.Text.Json;
using System.Text.Json.Serialization;
using Pondhawk.Logging.Utilities;

namespace Pondhawk.Logging.Serializers;

/// <summary>
/// JSON converter for Type objects.
/// </summary>
internal sealed class TypeJsonConverter : JsonConverter<Type>
{
    public override Type? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        throw new NotSupportedException("Type deserialization is not supported");
    }

    public override void Write(Utf8JsonWriter writer, Type value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WriteString("Name", value.GetConciseFullName());
        writer.WriteEndObject();
    }
}
