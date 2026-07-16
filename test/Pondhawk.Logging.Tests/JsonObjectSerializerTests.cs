using Shouldly;
using Xunit;

namespace Pondhawk.Logging.Tests;

public class JsonObjectSerializerTests
{
    private sealed class WithSensitiveString
    {
        public string Username { get; set; } = "";

        [Sensitive]
        public string Password { get; set; } = "";
    }

    private sealed class WithSensitiveInt
    {
        public string Name { get; set; } = "";

        [Sensitive]
        public int AccountNumber { get; set; }
    }

    private sealed class WithSensitiveGuid
    {
        public string Name { get; set; } = "";

        [Sensitive]
        public Guid Token { get; set; }
    }

    [Fact]
    public void Serialize_SensitiveString_MasksValueKeepsRest()
    {
        var (_, json) = JsonObjectSerializer.Instance.Serialize(
            new WithSensitiveString { Username = "jsmith", Password = "hunter2" });

        json.ShouldContain("jsmith");
        json.ShouldContain("Sensitive - HasValue: true");
        json.ShouldNotContain("hunter2");
    }

    [Fact]
    public void Serialize_SensitiveInt_DoesNotCollapseWholeObject()
    {
        // Regression: a non-string [Sensitive] property returned a string in place of an int,
        // which threw during writing and collapsed the entire payload to "{}".
        var (_, json) = JsonObjectSerializer.Instance.Serialize(
            new WithSensitiveInt { Name = "widget", AccountNumber = 12345 });

        json.ShouldNotBe("{}");
        json.ShouldContain("widget");
        json.ShouldContain("Sensitive - HasValue: true");
        json.ShouldNotContain("12345");
    }

    [Fact]
    public void Serialize_SensitiveGuid_DoesNotCollapseWholeObject()
    {
        var token = Guid.NewGuid();

        var (_, json) = JsonObjectSerializer.Instance.Serialize(
            new WithSensitiveGuid { Name = "widget", Token = token });

        json.ShouldNotBe("{}");
        json.ShouldContain("widget");
        json.ShouldContain("Sensitive - HasValue: true");
        json.ShouldNotContain(token.ToString());
    }
}
