using System.Drawing;
using Serilog.Events;
using Shouldly;
using Xunit;

namespace Pondhawk.Logging.Watch.Tests.Switching;

public class SwitchTests
{

    [Fact]
    public void Create_ReturnsNewInstanceWithDefaults()
    {
        var sw = Switch.Create();

        sw.Pattern.ShouldBe("");
        sw.Level.ShouldBe(LogEventLevel.Error);
        sw.Color.ShouldBe(Color.White);
        sw.Tag.ShouldBe("");
    }

    [Fact]
    public void Initializer_SetsPattern()
    {
        var sw = new Switch { Pattern = "MyApp.Services" };

        sw.Pattern.ShouldBe("MyApp.Services");
    }

    [Fact]
    public void Initializer_SetsLevel()
    {
        var sw = new Switch { Level = LogEventLevel.Debug };

        sw.Level.ShouldBe(LogEventLevel.Debug);
    }

    [Fact]
    public void Initializer_SetsColor()
    {
        var sw = new Switch { Color = Color.Red };

        sw.Color.ShouldBe(Color.Red);
    }

    [Fact]
    public void Initializer_SetsTag()
    {
        var sw = new Switch { Tag = "Infrastructure" };

        sw.Tag.ShouldBe("Infrastructure");
    }

    [Fact]
    public void Initializer_SetsAllProperties()
    {
        var sw = new Switch
        {
            Pattern = "MyApp",
            Level = LogEventLevel.Warning,
            Color = Color.Blue,
            Tag = "Test"
        };

        sw.Pattern.ShouldBe("MyApp");
        sw.Level.ShouldBe(LogEventLevel.Warning);
        sw.Color.ShouldBe(Color.Blue);
        sw.Tag.ShouldBe("Test");
    }

}
