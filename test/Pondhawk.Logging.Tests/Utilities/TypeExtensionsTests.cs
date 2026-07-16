// Copyright (c) Pond Hawk Technologies Inc. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using Pondhawk.Logging.Utilities;
using Shouldly;
using Xunit;

namespace Pondhawk.Logging.Tests.Utilities;

public class TypeExtensionsTests
{
    // ── GetConciseName ──

    [Fact]
    public void GetConciseName_NonGeneric_ReturnsSimpleName()
    {
        typeof(string).GetConciseName().ShouldBe("String");
    }

    [Fact]
    public void GetConciseName_Generic_UsesAngleBrackets()
    {
        typeof(List<string>).GetConciseName().ShouldBe("List<String>");
    }

    [Fact]
    public void GetConciseName_NestedGeneric_RecursesIntoArguments()
    {
        typeof(Dictionary<string, List<int>>).GetConciseName()
            .ShouldBe("Dictionary<String, List<Int32>>");
    }

    // ── GetConciseFullName ──

    [Fact]
    public void GetConciseFullName_NonGeneric_IncludesNamespace()
    {
        typeof(string).GetConciseFullName().ShouldBe("System.String");
    }

    [Fact]
    public void GetConciseFullName_Generic_UsesAngleBracketsWithNamespace()
    {
        typeof(List<string>).GetConciseFullName()
            .ShouldBe("System.Collections.Generic.List<String>");
    }

    // ── Caching ──

    [Fact]
    public void GetConciseName_IsCached_ReturnsSameStringInstance()
    {
        var first = typeof(List<int>).GetConciseName();
        var second = typeof(List<int>).GetConciseName();

        // The cache returns the identical string instance on repeat lookups.
        ReferenceEquals(first, second).ShouldBeTrue();
    }

    [Fact]
    public void GetConciseFullName_IsCached_ReturnsSameStringInstance()
    {
        var first = typeof(Dictionary<string, int>).GetConciseFullName();
        var second = typeof(Dictionary<string, int>).GetConciseFullName();

        ReferenceEquals(first, second).ShouldBeTrue();
    }
}
