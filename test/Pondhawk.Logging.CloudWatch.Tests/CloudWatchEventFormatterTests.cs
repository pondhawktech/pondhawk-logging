// Copyright (c) Pond Hawk Technologies Inc. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Shouldly;
using Xunit;

namespace Pondhawk.Logging.CloudWatch.Tests;

public class CloudWatchEventFormatterTests
{
    private static CloudWatchEvent Event(Action<CloudWatchEvent> configure = null, Exception exception = null, LogLevel level = LogLevel.Error)
    {
        var logEvent = new CloudWatchEvent
        {
            Level = level,
            Category = "My.Category",
            Title = "it broke",
            CorrelationId = "corr-1",
            Occurred = DateTime.UtcNow,
            Exception = exception,
        };

        configure?.Invoke(logEvent);
        return logEvent;
    }

    private static JsonElement Parse(CloudWatchEvent logEvent)
    {
        using var document = JsonDocument.Parse(CloudWatchEventFormatter.Format(logEvent));
        return document.RootElement.Clone();
    }

    private static Exception Thrown(Exception exception)
    {
        try
        {
            throw exception;
        }
        catch (Exception caught)
        {
            return caught;
        }
    }

    private sealed class DeepException(int frames) : Exception("deep")
    {
        public override string StackTrace { get; } =
            string.Join('\n', Enumerable.Range(0, frames).Select(i => $"   at Some.Namespace.With.A.Long.Name.Type{i}.Method{i}(String argument) in /src/some/long/path/File{i}.cs:line {i}"));
    }

    [Fact]
    public void EachEventIsOneJsonObject_InsightsCanFilterOn()
    {
        var json = Parse(Event(level: LogLevel.Information));

        json.GetProperty("Level").GetString().ShouldBe("Information");
        json.GetProperty("Category").GetString().ShouldBe("My.Category");
        json.GetProperty("CorrelationId").GetString().ShouldBe("corr-1");
        json.GetProperty("Title").GetString().ShouldBe("it broke");
    }

    [Fact]
    public void SubjectAndTenant_AreFields_InsightsCanFilterOn()
    {
        var json = Parse(new CloudWatchEvent { Level = LogLevel.Warning, Category = "C", Title = "t", Subject = "kchen", Tenant = "acme" });

        json.GetProperty("Subject").GetString().ShouldBe("kchen");
        json.GetProperty("Tenant").GetString().ShouldBe("acme");
    }

    [Fact]
    public void AnEventWithoutPayloadCorrelationOrNesting_LeavesThemOut()
    {
        var json = Parse(new CloudWatchEvent { Level = LogLevel.Warning, Category = "C", Title = "t" });

        json.TryGetProperty("CorrelationId", out _).ShouldBeFalse();
        json.TryGetProperty("Subject", out _).ShouldBeFalse();
        json.TryGetProperty("Tenant", out _).ShouldBeFalse();
        json.TryGetProperty("Payload", out _).ShouldBeFalse();
        json.TryGetProperty("Context", out _).ShouldBeFalse();
        json.TryGetProperty("Exceptions", out _).ShouldBeFalse();
        json.TryGetProperty("Nesting", out _).ShouldBeFalse();
    }

    [Fact]
    public void AMethodTracingEvent_CarriesItsNestingDelta()
    {
        Parse(Event(e => e.Nesting = -1)).GetProperty("Nesting").GetInt32().ShouldBe(-1);
    }

    [Fact]
    public void APayloadTypedJson_IsNestedJson_NotAString()
    {
        var json = Parse(Event(e =>
        {
            e.PayloadType = PayloadType.Json;
            e.Payload = """{"OrderId":42,"Lines":[1,2]}""";
        }));

        json.GetProperty("Payload").GetProperty("OrderId").GetInt32().ShouldBe(42);
        json.GetProperty("Payload").GetProperty("Lines").GetArrayLength().ShouldBe(2);
    }

    [Fact]
    public void APayloadTypedJson_ThatDoesNotParse_StaysText()
    {
        var json = Parse(Event(e =>
        {
            e.PayloadType = PayloadType.Json;
            e.Payload = "{ not json";
        }));

        json.GetProperty("Payload").GetString().ShouldBe("{ not json");
    }

    [Fact]
    public void AMultiLinePayload_IsAnArrayOfLines()
    {
        var json = Parse(Event(e =>
        {
            e.PayloadType = PayloadType.Sql;
            e.Payload = "select *\r\nfrom orders\nwhere id = 1\n";
        }));

        json.GetProperty("Payload").EnumerateArray().Select(l => l.GetString())
            .ShouldBe(["select *", "from orders", "where id = 1"]);
    }

    [Fact]
    public void AnException_IsStructured_WithItsContextOnce()
    {
        var cause = Thrown(new InvalidOperationException("bad state"));

        var message = CloudWatchEventFormatter.Format(Event(e => e.ErrorContext = """{"OrderId":42}""", cause));
        using var document = JsonDocument.Parse(message);
        var json = document.RootElement;

        json.GetProperty("Context").GetProperty("OrderId").GetInt32().ShouldBe(42);
        json.TryGetProperty("Payload", out _).ShouldBeFalse();

        var exception = json.GetProperty("Exceptions")[0];
        exception.GetProperty("Type").GetString().ShouldBe("System.InvalidOperationException");
        exception.GetProperty("Message").GetString().ShouldBe("bad state");
        exception.GetProperty("StackTrace")[0].GetString().ShouldStartWith("at ");

        // Once: as the nested object, not repeated inside any text.
        message.Split("OrderId").Length.ShouldBe(2);
    }

    [Fact]
    public void TheWholeChainIsWritten_OutermostFirst()
    {
        var inner = Thrown(new ArgumentException("innermost"));
        var middle = Thrown(new InvalidOperationException("middle", inner));
        var outer = Thrown(new ApplicationException("outermost", middle));

        var exceptions = Parse(Event(exception: outer)).GetProperty("Exceptions");

        exceptions.EnumerateArray().Select(e => e.GetProperty("Message").GetString())
            .ShouldBe(["outermost", "middle", "innermost"]);
    }

    [Theory]
    [InlineData("\"just text\"")]
    [InlineData("42")]
    [InlineData("[1,2,3]")]
    public void AContextThatIsNotAnObject_IsWrappedInOne(string context)
    {
        var json = Parse(Event(e => e.ErrorContext = context, Thrown(new InvalidOperationException("x"))));

        json.GetProperty("Context").ValueKind.ShouldBe(JsonValueKind.Object);
        json.GetProperty("Context").GetProperty("Value").GetRawText().ShouldBe(context);
    }

    [Fact]
    public void AHugePayload_IsCut_AndTheEventStillFits()
    {
        var payload = string.Join('\n', Enumerable.Range(0, 20_000).Select(i => $"line {i} " + new string('x', 40)));

        var message = CloudWatchEventFormatter.Format(Event(e =>
        {
            e.PayloadType = PayloadType.Text;
            e.Payload = payload;
        }));

        Encoding.UTF8.GetByteCount(message).ShouldBeLessThanOrEqualTo(CloudWatchEventFormatter.MaxEventBytes);
        using var document = JsonDocument.Parse(message);
        document.RootElement.GetProperty("Payload").EnumerateArray().Last().GetString().ShouldBe("...[truncated]");
        document.RootElement.GetProperty("Title").GetString().ShouldBe("it broke");
    }

    [Fact]
    public void AJsonPayloadTooLargeForOneEvent_IsCutAsText()
    {
        var payload = "{\"Blob\":\"" + new string('x', 400_000) + "\"}";

        var message = CloudWatchEventFormatter.Format(Event(e =>
        {
            e.PayloadType = PayloadType.Json;
            e.Payload = payload;
        }));

        Encoding.UTF8.GetByteCount(message).ShouldBeLessThanOrEqualTo(CloudWatchEventFormatter.MaxEventBytes);
        using var document = JsonDocument.Parse(message);
        document.RootElement.GetProperty("Payload").GetString().ShouldEndWith("...[truncated]");
    }

    [Theory]
    [InlineData(300_000, 'x')]   // one long line, then another
    [InlineData(45_000, '<')]    // far shorter, but every character is escaped to six bytes
    [InlineData(45_000, '\u4e2d')]
    public async Task ALongFirstLine_FollowedByAnother_IsCut_AndFormattingReturns(int length, char character)
    {
        var logEvent = Event(e =>
        {
            e.PayloadType = PayloadType.Text;
            e.Payload = new string(character, length) + "\nsecond line\nthird line";
        }, level: LogLevel.Warning);

        // This once never returned. Bounded here so that a regression fails rather than hangs the run.
        var message = await Task.Run(() => CloudWatchEventFormatter.Format(logEvent)).WaitAsync(TimeSpan.FromSeconds(20));

        Encoding.UTF8.GetByteCount(message).ShouldBeLessThanOrEqualTo(CloudWatchEventFormatter.MaxEventBytes);
        using var document = JsonDocument.Parse(message);
        document.RootElement.GetProperty("Payload").GetString().ShouldEndWith("...[truncated]");
    }

    [Fact]
    public void AHugeTitle_IsCut()
    {
        var message = CloudWatchEventFormatter.Format(new CloudWatchEvent { Level = LogLevel.Error, Category = "My.Category", Title = new string('t', 2_000_000) });

        Encoding.UTF8.GetByteCount(message).ShouldBeLessThanOrEqualTo(CloudWatchEventFormatter.MaxEventBytes);
        using var document = JsonDocument.Parse(message);
        var title = document.RootElement.GetProperty("Title").GetString();
        title.ShouldEndWith("...[truncated]");
        title.Length.ShouldBeLessThan(CloudWatchEventFormatter.MaxTitleChars + 32);
    }

    [Fact]
    public void AHugeCategoryCorrelationIdSubjectAndTenant_AreCut()
    {
        var message = CloudWatchEventFormatter.Format(new CloudWatchEvent
        {
            Level = LogLevel.Warning,
            Category = new string('c', 1_000_000),
            CorrelationId = new string('i', 1_000_000),
            Subject = new string('s', 1_000_000),
            Tenant = new string('t', 1_000_000),
            Title = "t",
        });

        Encoding.UTF8.GetByteCount(message).ShouldBeLessThan(8_192);
    }

    private sealed class HugeMessageException(Exception inner = null) : Exception("ignored", inner)
    {
        public override string Message => new string('m', 2_000_000);
    }

    private sealed class ThrowingException : Exception
    {
        public override string Message => throw new NotSupportedException("message");

        public override string StackTrace => throw new NotSupportedException("stack");
    }

    [Fact]
    public void AHugeExceptionMessage_IsCut_AndTheExceptionStillReported()
    {
        var message = CloudWatchEventFormatter.Format(Event(exception: new HugeMessageException()));

        Encoding.UTF8.GetByteCount(message).ShouldBeLessThanOrEqualTo(CloudWatchEventFormatter.MaxEventBytes);
        using var document = JsonDocument.Parse(message);
        var exception = document.RootElement.GetProperty("Exceptions")[0];
        exception.GetProperty("Type").GetString().ShouldEndWith("HugeMessageException");
        exception.GetProperty("Message").GetString().ShouldEndWith("...[truncated]");
    }

    [Fact]
    public void AChainLongerThanAnythingReal_IsCut()
    {
        Exception chain = new InvalidOperationException("leaf");
        for (var i = 0; i < 20_000; i++)
            chain = new InvalidOperationException("level " + i, chain);

        var message = CloudWatchEventFormatter.Format(Event(exception: chain));

        Encoding.UTF8.GetByteCount(message).ShouldBeLessThanOrEqualTo(CloudWatchEventFormatter.MaxEventBytes);
        using var document = JsonDocument.Parse(message);
        var exceptions = document.RootElement.GetProperty("Exceptions");
        exceptions.GetArrayLength().ShouldBe(CloudWatchEventFormatter.MaxExceptions + 1);
        exceptions[0].GetProperty("Message").GetString().ShouldBe("level 19999");
        exceptions.EnumerateArray().Last().GetProperty("Message").GetString().ShouldContain("further inner exceptions cut");
    }

    [Fact]
    public void WhenNothingCanBeCutToFit_TheEventIsStillReported_MarkedTruncated()
    {
        // Thirty-two inner exceptions, each with a message at its cap and escaped six-fold: over the limit
        // with every stack frame gone.
        Exception chain = null;
        for (var i = 0; i < 40; i++)
            chain = new InvalidOperationException(new string('<', 20_000), chain);

        var message = CloudWatchEventFormatter.Format(new CloudWatchEvent
        {
            Level = LogLevel.Error, Category = "My.Category", Title = "it broke", Subject = "kchen", Tenant = "acme", Exception = chain,
        });

        Encoding.UTF8.GetByteCount(message).ShouldBeLessThanOrEqualTo(CloudWatchEventFormatter.MaxEventBytes);
        using var document = JsonDocument.Parse(message);
        document.RootElement.GetProperty("Truncated").GetBoolean().ShouldBeTrue();
        document.RootElement.GetProperty("Subject").GetString().ShouldBe("kchen", "who it was for survives truncation");
        document.RootElement.GetProperty("Tenant").GetString().ShouldBe("acme");
        document.RootElement.GetProperty("Title").GetString().ShouldBe("it broke");
        document.RootElement.GetProperty("Exceptions")[0].GetProperty("Type").GetString().ShouldBe("System.InvalidOperationException");
    }

    [Fact]
    public void AnExceptionWhoseGettersThrow_IsReported_NotThrown()
    {
        var message = Should.NotThrow(() => CloudWatchEventFormatter.Format(Event(exception: new ThrowingException())));

        using var document = JsonDocument.Parse(message);
        var exception = document.RootElement.GetProperty("Exceptions")[0];
        exception.GetProperty("Type").GetString().ShouldEndWith("ThrowingException");
        exception.GetProperty("Message").GetString().ShouldContain("Message threw NotSupportedException");
        exception.GetProperty("StackTrace").GetArrayLength().ShouldBe(0);
    }

    [Theory]
    [InlineData(63)]
    [InlineData(64)]
    [InlineData(65)]
    [InlineData(500)]
    public void AContextNestedAtTheParserLimit_IsNotThrown(int depth)
    {
        var context = new string('[', depth) + new string(']', depth);

        var message = Should.NotThrow(() => CloudWatchEventFormatter.Format(Event(e => e.ErrorContext = context, Thrown(new InvalidOperationException("x")))));

        using var document = JsonDocument.Parse(message, new JsonDocumentOptions { MaxDepth = 1_024 });
        document.RootElement.GetProperty("Exceptions").GetArrayLength().ShouldBe(1);
    }

    [Fact]
    public void ADeepStack_IsCutToFit_AndSaysHowMuch()
    {
        var message = CloudWatchEventFormatter.Format(Event(exception: new DeepException(5_000)));

        Encoding.UTF8.GetByteCount(message).ShouldBeLessThanOrEqualTo(CloudWatchEventFormatter.MaxEventBytes);
        using var document = JsonDocument.Parse(message);
        var stack = document.RootElement.GetProperty("Exceptions")[0].GetProperty("StackTrace");
        stack[0].GetString().ShouldStartWith("at Some.Namespace");
        stack.EnumerateArray().Last().GetString().ShouldContain("more frame(s) cut");
    }
}
