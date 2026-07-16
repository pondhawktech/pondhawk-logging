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
