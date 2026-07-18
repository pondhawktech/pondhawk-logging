// Copyright (c) Pond Hawk Technologies Inc. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System.Drawing;
using Microsoft.Extensions.Logging;
using Shouldly;
using Xunit;

namespace Pondhawk.Logging.Watch.Tests.Switching;

public class SwitchSourceTests
{

    // --- Defaults ---

    [Fact]
    public void DefaultVersion_IsZero()
    {
        var source = new SwitchSource();

        source.Version.ShouldBe(0);
    }

    [Fact]
    public void DefaultSwitch_HasErrorLevel()
    {
        var source = new SwitchSource();

        source.DefaultSwitch.Level.ShouldBe(LogLevel.Error);
    }

    [Fact]
    public void GetDebugSwitch_HasDebugLevel()
    {
        var source = new SwitchSource();

        source.GetDebugSwitch().Level.ShouldBe(LogLevel.Debug);
    }

    // --- WhenNotMatched ---

    [Fact]
    public void WhenNotMatched_Level_SetsDefaultSwitch()
    {
        var source = new SwitchSource();

        source.WhenNotMatched(LogLevel.Information);

        source.DefaultSwitch.Level.ShouldBe(LogLevel.Information);
    }

    [Fact]
    public void WhenNotMatched_LevelAndColor_SetsDefaultSwitch()
    {
        var source = new SwitchSource();

        source.WhenNotMatched(LogLevel.Warning, Color.Yellow);

        source.DefaultSwitch.Level.ShouldBe(LogLevel.Warning);
        source.DefaultSwitch.Color.ShouldBe(Color.Yellow);
    }

    [Fact]
    public void WhenNotMatched_ReturnsSelf()
    {
        var source = new SwitchSource();

        var result = source.WhenNotMatched(LogLevel.Debug);

        result.ShouldBeSameAs(source);
    }

    // --- WhenMatched ---

    [Fact]
    public void WhenMatched_AddsSwitch()
    {
        var source = new SwitchSource();

        source.WhenMatched("MyApp", LogLevel.Debug, Color.Green);

        var sw = source.Lookup("MyApp.Services.Repo");
        sw.Level.ShouldBe(LogLevel.Debug);
        sw.Color.ShouldBe(Color.Green);
    }

    [Fact]
    public void WhenMatched_WithTag_SetsTagOnSwitch()
    {
        var source = new SwitchSource();

        source.WhenMatched("MyApp", "CustomTag", LogLevel.Debug, Color.Green);

        var sw = source.Lookup("MyApp.Services");
        sw.Tag.ShouldBe("CustomTag");
    }

    // --- Lookup ---

    [Fact]
    public void Lookup_NoSwitches_ReturnsDefaultSwitch()
    {
        var source = new SwitchSource();

        var sw = source.Lookup("AnyCategory");

        sw.ShouldBeSameAs(source.DefaultSwitch);
    }

    [Fact]
    public void Lookup_ExactMatch_ReturnsSwitch()
    {
        var source = new SwitchSource();
        source.WhenMatched("MyApp.Services", LogLevel.Debug, Color.Red);

        var sw = source.Lookup("MyApp.Services");

        sw.Level.ShouldBe(LogLevel.Debug);
        sw.Pattern.ShouldBe("MyApp.Services");
    }

    [Fact]
    public void Lookup_PrefixMatch_ReturnsSwitch()
    {
        var source = new SwitchSource();
        source.WhenMatched("MyApp", LogLevel.Information, Color.Blue);

        var sw = source.Lookup("MyApp.Services.Repo");

        sw.Level.ShouldBe(LogLevel.Information);
    }

    [Fact]
    public void Lookup_NoMatch_ReturnsDefaultSwitch()
    {
        var source = new SwitchSource();
        source.WhenMatched("MyApp", LogLevel.Debug, Color.Red);

        var sw = source.Lookup("OtherApp.Services");

        sw.ShouldBeSameAs(source.DefaultSwitch);
    }

    [Fact]
    public void Lookup_LongestPrefixWins()
    {
        var source = new SwitchSource();
        source.WhenMatched("MyApp", LogLevel.Warning, Color.Yellow);
        source.WhenMatched("MyApp.Services", LogLevel.Debug, Color.Green);

        var sw = source.Lookup("MyApp.Services.Repo");

        sw.Level.ShouldBe(LogLevel.Debug);
        sw.Pattern.ShouldBe("MyApp.Services");
    }

    [Fact]
    public void Lookup_NullCategory_Throws()
    {
        var source = new SwitchSource();

        Should.Throw<ArgumentException>(() => source.Lookup(null));
    }

    [Fact]
    public void Lookup_EmptyCategory_Throws()
    {
        var source = new SwitchSource();

        Should.Throw<ArgumentException>(() => source.Lookup(""));
    }

    [Fact]
    public void LookupColor_MatchingPattern_ReturnsColor()
    {
        var source = new SwitchSource();
        source.WhenMatched("MyApp", LogLevel.Debug, Color.Magenta);

        var color = source.LookupColor("MyApp.Something");

        color.ShouldBe(Color.Magenta);
    }

    [Fact]
    public void LookupColor_NoMatch_ReturnsDefaultColor()
    {
        var source = new SwitchSource();

        var color = source.LookupColor("Unknown.Category");

        color.ShouldBe(source.DefaultSwitch.Color);
    }

    // --- Update ---

    [Fact]
    public void Update_ReplacesAllSwitches()
    {
        var source = new SwitchSource();
        source.WhenMatched("OldPattern", LogLevel.Debug, Color.Red);

        var newDefs = new List<SwitchDef>
        {
            new() { Pattern = "NewPattern", Level = LogLevel.Warning, Color = Color.Blue }
        };

        source.Update(newDefs);

        source.Lookup("NewPattern.Sub").Level.ShouldBe(LogLevel.Warning);
        source.Lookup("OldPattern.Sub").ShouldBeSameAs(source.DefaultSwitch);
    }

    [Fact]
    public void Update_IncrementsVersion()
    {
        var source = new SwitchSource();
        source.Version.ShouldBe(0);

        source.Update(new List<SwitchDef>
        {
            new() { Pattern = "Test", Level = LogLevel.Debug, Color = Color.Red }
        });

        source.Version.ShouldBe(1);

        source.Update(new List<SwitchDef>());

        source.Version.ShouldBe(2);
    }

    [Fact]
    public void Update_Null_Throws()
    {
        var source = new SwitchSource();

        Should.Throw<ArgumentNullException>(() => source.Update(null));
    }

    [Fact]
    public void Update_EmptyList_ClearsAllSwitches()
    {
        var source = new SwitchSource();
        source.WhenMatched("MyApp", LogLevel.Debug, Color.Red);

        source.Update(new List<SwitchDef>());

        source.Lookup("MyApp.Service").ShouldBeSameAs(source.DefaultSwitch);
    }

}
