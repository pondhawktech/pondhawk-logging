// Copyright (c) Pond Hawk Technologies Inc. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System.Diagnostics;
using Shouldly;
using Xunit;

namespace Pondhawk.Logging.Tests;

public class DiagnosticProbeTests
{
    [Fact]
    public void Without_an_activity_nothing_is_probed_and_nothing_can_be()
    {
        Activity.Current = null;

        DiagnosticProbe.Begin().ShouldBeFalse();
        DiagnosticProbe.IsActive.ShouldBeFalse();
    }

    [Fact]
    public void A_probe_covers_its_activity_and_the_activities_under_it()
    {
        using var request = new Activity("request").Start();
        DiagnosticProbe.IsActive.ShouldBeFalse();

        DiagnosticProbe.Begin().ShouldBeTrue();
        DiagnosticProbe.IsActive.ShouldBeTrue();

        using (new Activity("query").Start())
            DiagnosticProbe.IsActive.ShouldBeTrue();
    }

    [Fact]
    public void A_probe_ends_with_its_activity()
    {
        var request = new Activity("request").Start();
        DiagnosticProbe.Begin();
        request.Stop();

        using var next = new Activity("next request").Start();

        DiagnosticProbe.IsActive.ShouldBeFalse();
    }

    [Fact]
    public void A_probe_is_neither_baggage_nor_a_tag_so_it_is_never_sent_on()
    {
        using var request = new Activity("request").Start();

        DiagnosticProbe.Begin();

        request.Baggage.ShouldBeEmpty();
        request.TagObjects.ShouldBeEmpty();
    }
}
