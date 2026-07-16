// Copyright (c) Pond Hawk Technologies Inc. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using Shouldly;
using Xunit;

namespace Pondhawk.Logging.Watch.Tests;

public class LogEventBatchTests
{

    [Fact]
    public void Empty_HasEmptyEventsAndDomain()
    {
        var batch = LogEventBatch.Empty;

        batch.Events.ShouldBeEmpty();
        batch.Domain.ShouldBe(string.Empty);
    }

    [Fact]
    public void Single_CreatesOneBatchWithOneEvent()
    {
        var ev = new LogEvent { Title = "test event" };

        var batch = LogEventBatch.Single("my-domain", ev);

        batch.Events.Count.ShouldBe(1);
        batch.Events[0].Title.ShouldBe("test event");
    }

    [Fact]
    public void Single_SetsDomain()
    {
        var batch = LogEventBatch.Single("my-domain", new LogEvent());

        batch.Domain.ShouldBe("my-domain");
    }

    [Fact]
    public void Default_Uid_IsNonEmpty()
    {
        var batch = new LogEventBatch();

        batch.Uid.ShouldNotBeNullOrWhiteSpace();
        batch.Uid.Length.ShouldBe(26); // ULID length
    }

    [Fact]
    public void Default_Events_IsEmptyList()
    {
        var batch = new LogEventBatch();

        batch.Events.ShouldNotBeNull();
        batch.Events.ShouldBeEmpty();
    }

    [Fact]
    public void TwoInstances_HaveDifferentUids()
    {
        var batch1 = new LogEventBatch();
        var batch2 = new LogEventBatch();

        batch1.Uid.ShouldNotBe(batch2.Uid);
    }

}
