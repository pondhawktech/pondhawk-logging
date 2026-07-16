// Copyright (c) Pond Hawk Technologies Inc. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using Shouldly;
using Xunit;

namespace Pondhawk.Logging.Watch.Tests;

public class LogEventTests
{

    [Fact]
    public void DefaultProperties_AreCorrectDefaults()
    {
        var le = new LogEvent();

        le.Category.ShouldBe(string.Empty);
        le.CorrelationId.ShouldBe(string.Empty);
        le.Title.ShouldBe(string.Empty);
        le.Tenant.ShouldBe(string.Empty);
        le.Subject.ShouldBe(string.Empty);
        le.Tag.ShouldBe(string.Empty);
        le.Level.ShouldBe(0);
        le.Color.ShouldBe(0);
        le.Nesting.ShouldBe(0);
        le.Type.ShouldBe(0);
        le.Payload.ShouldBeNull();
        le.ErrorType.ShouldBeNull();
        le.Object.ShouldBeNull();
        le.Error.ShouldBeNull();
        le.ErrorContext.ShouldBeNull();
    }

    [Fact]
    public void AllProperties_AreSettable()
    {
        var now = DateTime.UtcNow;
        var ex = new InvalidOperationException("test");
        var ctx = new object();
        var obj = new { Name = "Test" };

        var le = new LogEvent
        {
            Category = "MyApp.Service",
            CorrelationId = "corr-123",
            Title = "Something happened",
            Tenant = "tenant-1",
            Subject = "user-42",
            Tag = "infra",
            Level = 3,
            Color = 0xFF0000,
            Nesting = 1,
            Occurred = now,
            Type = 2,
            Payload = "{\"key\":\"value\"}",
            ErrorType = "InvalidOperationException",
            Object = obj,
            Error = ex,
            ErrorContext = ctx
        };

        le.Category.ShouldBe("MyApp.Service");
        le.CorrelationId.ShouldBe("corr-123");
        le.Title.ShouldBe("Something happened");
        le.Tenant.ShouldBe("tenant-1");
        le.Subject.ShouldBe("user-42");
        le.Tag.ShouldBe("infra");
        le.Level.ShouldBe(3);
        le.Color.ShouldBe(0xFF0000);
        le.Nesting.ShouldBe(1);
        le.Occurred.ShouldBe(now);
        le.Type.ShouldBe(2);
        le.Payload.ShouldBe("{\"key\":\"value\"}");
        le.ErrorType.ShouldBe("InvalidOperationException");
        le.Object.ShouldBeSameAs(obj);
        le.Error.ShouldBeSameAs(ex);
        le.ErrorContext.ShouldBeSameAs(ctx);
    }

    [Fact]
    public void Occurred_CanBeSetAndRead()
    {
        var le = new LogEvent();
        var ts = new DateTime(2025, 6, 15, 12, 0, 0, DateTimeKind.Utc);

        le.Occurred = ts;

        le.Occurred.ShouldBe(ts);
    }

}
