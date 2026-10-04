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
    public void AnEventWithoutPayloadCorrelationOrNesting_LeavesThemOut()
    {
        var json = Parse(new CloudWatchEvent { Level = LogLevel.Warning, Category = "C", Title = "t" });

        json.TryGetProperty("CorrelationId", out _).ShouldBeFalse();
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
