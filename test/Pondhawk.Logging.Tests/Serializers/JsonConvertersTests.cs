using System.Text.Json;
using Pondhawk.Logging.Serializers;
using Shouldly;
using Xunit;

namespace Pondhawk.Logging.Tests.Serializers;

public class JsonConvertersTests
{
    // ── AttributeJsonConverter ──

    [Fact]
    public void AttributeConverter_Write_EmitsConciseFullNameObject()
    {
        var options = new JsonSerializerOptions { Converters = { new AttributeJsonConverter() } };

        var json = JsonSerializer.Serialize<Attribute>(new ObsoleteAttribute("deprecated"), options);

        json.ShouldBe("{\"Name\":\"System.ObsoleteAttribute\"}");
    }

    [Fact]
    public void AttributeConverter_Read_IsNotSupported()
    {
        var options = new JsonSerializerOptions { Converters = { new AttributeJsonConverter() } };

        Should.Throw<NotSupportedException>(
            () => JsonSerializer.Deserialize<Attribute>("{}", options));
    }

    // ── TypeJsonConverter ──

    [Fact]
    public void TypeConverter_Write_EmitsConciseFullNameObject()
    {
        var options = new JsonSerializerOptions { Converters = { new TypeJsonConverter() } };

        var json = JsonSerializer.Serialize(typeof(string), options);

        json.ShouldBe("{\"Name\":\"System.String\"}");
    }

    [Fact]
    public void TypeConverter_Read_IsNotSupported()
    {
        var options = new JsonSerializerOptions { Converters = { new TypeJsonConverter() } };

        Should.Throw<NotSupportedException>(
            () => JsonSerializer.Deserialize<Type>("{}", options));
    }
}
