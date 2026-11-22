// Copyright (c) Pond Hawk Technologies Inc. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System.Diagnostics;
using Shouldly;
using Xunit;

namespace Pondhawk.Logging.Tests;

public class RetroLoggingTests
{
    [Fact]
    public void Without_an_activity_nothing_is_marked_and_nothing_can_be()
    {
        Activity.Current = null;

        RetroLogging.Begin().ShouldBeFalse();
        RetroLogging.IsActive.ShouldBeFalse();
    }

    [Fact]
    public void A_mark_covers_its_activity_and_the_activities_under_it()
    {
        using var request = new Activity("request").Start();
        RetroLogging.IsActive.ShouldBeFalse();

        RetroLogging.Begin().ShouldBeTrue();
        RetroLogging.IsActive.ShouldBeTrue();

        using (new Activity("query").Start())
            RetroLogging.IsActive.ShouldBeTrue();
    }

    [Fact]
    public void A_mark_ends_with_its_activity()
    {
        var request = new Activity("request").Start();
        RetroLogging.Begin();
        request.Stop();

        using var next = new Activity("next request").Start();

        RetroLogging.IsActive.ShouldBeFalse();
    }

    [Fact]
    public void A_mark_is_not_a_probe()
    {
        using var request = new Activity("request").Start();

        RetroLogging.Begin();

        DiagnosticProbe.IsActive.ShouldBeFalse();
        request.Baggage.ShouldBeEmpty();
        request.TagObjects.ShouldBeEmpty();
    }
}
