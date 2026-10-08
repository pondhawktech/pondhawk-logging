// Copyright (c) Pond Hawk Technologies Inc. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System.Diagnostics;
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
    public void SetSubjectAndTenant_CoverTheUnitOfWork()
    {
        using var scope = CorrelationManager.Begin();

        CorrelationManager.SetSubject("kchen");
        CorrelationManager.SetTenant("acme");

        CorrelationManager.Subject.ShouldBe("kchen");
        CorrelationManager.Tenant.ShouldBe("acme");
    }

    [Fact]
    public void SubjectAndTenant_End_WithTheScope()
    {
        var scope = CorrelationManager.Begin();
        CorrelationManager.SetSubject("kchen");
        CorrelationManager.SetTenant("acme");

        scope.Dispose();

        CorrelationManager.Subject.ShouldBeNull();
        CorrelationManager.Tenant.ShouldBeNull();
    }

    [Fact]
    public void SubjectAndTenant_ReachChildActivities()
    {
        using var scope = CorrelationManager.Begin();
        CorrelationManager.SetSubject("kchen");
        CorrelationManager.SetTenant("acme");

        using var child = new Activity("child").Start();   // e.g. an outgoing HTTP call's activity

        CorrelationManager.Subject.ShouldBe("kchen");
        CorrelationManager.Tenant.ShouldBe("acme");
    }

    [Fact]
    public void Subject_SetInAnInnerScope_WinsThere_AndTheOuterReturnsAfter()
    {
        using var outer = CorrelationManager.Begin();
        CorrelationManager.SetSubject("outer");

        using (CorrelationManager.Begin())
        {
            CorrelationManager.Subject.ShouldBe("outer", "inherited until set");
            CorrelationManager.SetSubject("inner");
            CorrelationManager.Subject.ShouldBe("inner");
            CorrelationManager.SetSubject(null);
            CorrelationManager.Subject.ShouldBe("outer", "null falls back to the enclosing scope");
        }

        CorrelationManager.Subject.ShouldBe("outer");
    }

    [Fact]
    public void SubjectAndTenant_AreNotBaggage_SoTheyNeverLeaveTheProcess()
    {
        using var scope = CorrelationManager.Begin();
        CorrelationManager.SetSubject("kchen");
        CorrelationManager.SetTenant("acme");

        Activity.Current!.Baggage.ShouldAllBe(item => item.Key == CorrelationManager.BaggageKey);
    }

    [Fact]
    public void SetSubject_WithNoActivity_IsIgnored()
    {
        Activity.Current.ShouldBeNull();

        Should.NotThrow(() => CorrelationManager.SetSubject("kchen"));
        CorrelationManager.Subject.ShouldBeNull();
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
