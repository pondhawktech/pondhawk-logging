// Copyright (c) Pond Hawk Technologies Inc. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System.Text.Json;
using System.Text.Json.Serialization;
using Pondhawk.Logging.Utilities;

namespace Pondhawk.Logging.Serializers;

/// <summary>
/// JSON converter for Attribute objects.
/// </summary>
internal sealed class AttributeJsonConverter : JsonConverter<Attribute>
{
    public override Attribute? Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        throw new NotSupportedException("Attribute deserialization is not supported");
    }

    public override void Write(Utf8JsonWriter writer, Attribute value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WriteString("Name", value.GetType().GetConciseFullName());
        writer.WriteEndObject();
    }
}
