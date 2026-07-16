// Copyright (c) Pond Hawk Technologies Inc. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using Pondhawk.Logging;
using Shouldly;
using Xunit;

namespace Pondhawk.Logging.Tests;

public class CorrelationManagerTests
{

    [Fact]
    public void BaggageKey_IsExpectedValue()
    {
        CorrelationManager.BaggageKey.ShouldBe("pondhawk.correlation");
    }

    [Fact]
    public void Begin_GeneratesCorrelationId()
    {
        using var scope = CorrelationManager.Begin();

        var current = CorrelationManager.Current;
        current.ShouldNotBeNull();
        current.Length.ShouldBe(26);
    }

    [Fact]
    public void Begin_WithExplicitId_SetsCorrelationId()
    {
        using var scope = CorrelationManager.Begin("my-custom-id");

        CorrelationManager.Current.ShouldBe("my-custom-id");
    }

    [Fact]
    public void Begin_Dispose_ClearsCorrelation()
    {
        var scope = CorrelationManager.Begin();
        CorrelationManager.Current.ShouldNotBeNull();

        scope.Dispose();

        CorrelationManager.Current.ShouldBeNull();
    }

    [Fact]
    public void Begin_NestedScopes_InnerOverridesOuter()
    {
        using var outer = CorrelationManager.Begin("outer-id");
        CorrelationManager.Current.ShouldBe("outer-id");

        var inner = CorrelationManager.Begin("inner-id");
        CorrelationManager.Current.ShouldBe("inner-id");

        inner.Dispose();
        CorrelationManager.Current.ShouldBe("outer-id");
    }

    [Fact]
    public void Set_OverridesCurrentCorrelation()
    {
        using var scope = CorrelationManager.Begin("original");

        CorrelationManager.Set("overridden");

        CorrelationManager.Current.ShouldBe("overridden");
    }

    [Fact]
    public void Set_Null_GeneratesNewUlid()
    {
        using var scope = CorrelationManager.Begin("original");

        CorrelationManager.Set(null);

        var current = CorrelationManager.Current;
        current.ShouldNotBeNull();
        current.ShouldNotBe("original");
        current.Length.ShouldBe(26);
    }

    [Fact]
    public void CorrelationScope_DoubleDispose_DoesNotThrow()
    {
        var scope = CorrelationManager.Begin();

        Should.NotThrow(() =>
        {
            scope.Dispose();
            scope.Dispose();
        });
    }

}
