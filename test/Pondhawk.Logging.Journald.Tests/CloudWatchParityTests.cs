// Copyright (c) Pond Hawk Technologies Inc. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

extern alias CloudWatch;

using System.Text.Json.Nodes;
using Microsoft.Extensions.Logging;
using Pondhawk.Logging.Journald.Tests.Support;
using Shouldly;
using Xunit;
using CloudWatchEventFormatter = CloudWatch::Pondhawk.Logging.CloudWatch.CloudWatchEventFormatter;

namespace Pondhawk.Logging.Journald.Tests;

/// <summary>
/// The journal's fields, turned back into JSON the way <c>pondhawk-logship</c> does, are the JSON the CloudWatch
/// provider writes for the same event — so one Logs Insights query works for both.
/// </summary>
public class CloudWatchParityTests
{
    private sealed class DeepException(int frames, string message = "deep", Exception inner = null) : Exception(message, inner)
    {
        public override string StackTrace { get; } =
            string.Join('\n', Enumerable.Range(0, frames).Select(i => $"   at Some.Type{i}.Method{i}(String a) in /src/File{i}.cs:line {i}"));
    }

    public static TheoryData<string, TestEvent> Events()
    {
        Exception longChain = new DeepException(5);
        for (var i = 0; i < 40; i++)
            longChain = new DeepException(3, $"m{i}", longChain);

        var data = new TheoryData<string, TestEvent>
        {
            { "plain", new TestEvent { Level = LogLevel.Information } },
            { "no correlation", new TestEvent { CorrelationId = null } },
            { "subject and tenant", new TestEvent { Subject = "alice", Tenant = "acme", CorrelationId = "  " } },
            { "method entry", new TestEvent { Level = LogLevel.Debug, Nesting = 1 } },
            { "method exit", new TestEvent { Level = LogLevel.Debug, Nesting = -1 } },
            { "json payload", new TestEvent { PayloadType = 1, Payload = "{\"a\":1,\"b\":[1,2]}" } },
            { "json that does not parse", new TestEvent { PayloadType = 1, Payload = "{not json" } },
            { "sql payload", new TestEvent { PayloadType = 2, Payload = "SELECT *\r\nFROM Orders\n" } },
            { "one-line text", new TestEvent { PayloadType = 4, Payload = "single" } },
            { "context object", new TestEvent { Context = "{\"Id\":5}" } },
            { "context scalar", new TestEvent { Context = "42" } },
            { "context and payload", new TestEvent { Context = "[1,2]", PayloadType = 4, Payload = "p" } },
            { "exception chain with context", new TestEvent { Context = "{\"Id\":5}", Exception = TestEvent.Thrown(new InvalidOperationException("bad", TestEvent.Thrown(new ArgumentException("inner")))) } },
            { "exception with payload", new TestEvent { PayloadType = 1, Payload = "{\"a\":1}", Exception = new InvalidOperationException("with payload") } },
            { "chain over the cap", new TestEvent { Exception = longChain } },
            { "multi-line title", new TestEvent { Title = "first\nsecond" } },
        };
        return data;
    }

    [Theory]
    [MemberData(nameof(Events))]
    public void TheFields_AreTheCloudWatchJson(string name, TestEvent logEvent)
    {
        var cloudWatch = JsonNode.Parse(CloudWatchEventFormatter.Format(logEvent.CloudWatch()))!.AsObject();
        var journal = Logship(JournalMessage.Map(JournalEventWriter.Format(logEvent.Journald(), "my-tool")));

        JsonNode.DeepEquals(journal, cloudWatch).ShouldBeTrue(
            $"{name}:{Environment.NewLine}journal    {journal.ToJsonString()}{Environment.NewLine}cloudwatch {cloudWatch.ToJsonString()}");
    }

    /// <summary>
    /// The fields as <c>pondhawk-logship</c> turns them back into an event: <c>_JSON</c> fields parsed as nested
    /// JSON, <c>PAYLOAD</c> a string if it is one line and an array of lines otherwise, and <c>TRUNCATED</c> left
    /// out, since CloudWatch marks only the minimal event.
    /// </summary>
    private static JsonObject Logship(Dictionary<string, string> fields)
    {
        var json = new JsonObject
        {
            ["Level"] = fields["PRIORITY"] switch
            {
                "2" => "Critical",
                "3" => "Error",
                "4" => "Warning",
                "6" => "Information",
                _ => "Debug",
            },
            ["Category"] = fields["CATEGORY"],
        };

        if (fields.TryGetValue("CORRELATION_ID", out var correlationId))
            json["CorrelationId"] = correlationId;
        if (fields.TryGetValue("SUBJECT", out var subject))
            json["Subject"] = subject;
        if (fields.TryGetValue("TENANT", out var tenant))
            json["Tenant"] = tenant;
        json["Title"] = fields["TITLE"];
        if (fields.TryGetValue("NESTING", out var nesting))
            json["Nesting"] = int.Parse(nesting, System.Globalization.CultureInfo.InvariantCulture);
        if (fields.TryGetValue("CONTEXT_JSON", out var context))
            json["Context"] = JsonNode.Parse(context);
        if (fields.TryGetValue("EXCEPTIONS_JSON", out var exceptions))
            json["Exceptions"] = JsonNode.Parse(exceptions);
        if (fields.TryGetValue("PAYLOAD_JSON", out var payloadJson))
            json["Payload"] = JsonNode.Parse(payloadJson);
        if (fields.TryGetValue("PAYLOAD", out var payload))
        {
            var lines = payload.Split('\n');
            json["Payload"] = lines.Length == 1 ? lines[0] : new JsonArray([.. lines.Select(l => (JsonNode)l)]);
        }

        return json;
    }
}
