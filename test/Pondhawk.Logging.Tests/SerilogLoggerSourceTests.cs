// Copyright (c) Pond Hawk Technologies Inc. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using Pondhawk.Logging;
using Pondhawk.Logging.Tests.Support;
using Shouldly;
using Xunit;

namespace Pondhawk.Logging.Tests;

public class SerilogLoggerSourceTests
{
    [Fact]
    public void CreateLogger_Generic_SetsSourceContextToConciseFullName()
    {
        var (root, sink) = CollectingSink.Build();
        var source = new SerilogLoggerSource(root);

        source.CreateLogger<SerilogLoggerSourceTests>().Information("hi");

        var e = sink.Events.ShouldHaveSingleItem();
        CollectingSink.Prop(e, "SourceContext").ShouldBe("Pondhawk.Logging.Tests.SerilogLoggerSourceTests");
    }

    [Fact]
    public void CreateLogger_String_SetsSourceContext()
    {
        var (root, sink) = CollectingSink.Build();
        var source = new SerilogLoggerSource(root);

        source.CreateLogger("My.Category").Information("hi");

        CollectingSink.Prop(sink.Events.ShouldHaveSingleItem(), "SourceContext").ShouldBe("My.Category");
    }

    [Fact]
    public void CreateLogger_Type_UsesConciseFullName()
    {
        var (root, sink) = CollectingSink.Build();
        var source = new SerilogLoggerSource(root);

        source.CreateLogger(typeof(SerilogLoggerSourceTests)).Information("hi");

        CollectingSink.Prop(sink.Events.ShouldHaveSingleItem(), "SourceContext")
            .ShouldBe("Pondhawk.Logging.Tests.SerilogLoggerSourceTests");
    }
}
