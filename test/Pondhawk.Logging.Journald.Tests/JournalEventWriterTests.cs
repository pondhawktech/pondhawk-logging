// Copyright (c) Pond Hawk Technologies Inc. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System.Buffers.Binary;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Pondhawk.Logging.Events;
using Pondhawk.Logging.Journald.Tests.Support;
using Shouldly;
using Xunit;

namespace Pondhawk.Logging.Journald.Tests;

public class JournalEventWriterTests
{
    private static readonly string[] AllowedFields =
    [
        "MESSAGE", "PRIORITY", "SYSLOG_IDENTIFIER", "CATEGORY", "TITLE", "CORRELATION_ID", "SUBJECT", "TENANT",
        "NESTING", "CONTEXT_JSON", "PAYLOAD_JSON", "PAYLOAD", "EXCEPTIONS_JSON", "TRUNCATED",
    ];

    private static byte[] Format(TestEvent logEvent) => JournalEventWriter.Format(logEvent.Journald(), "my-tool");

    private static Dictionary<string, string> Fields(TestEvent logEvent) => JournalMessage.Map(Format(logEvent));

    private sealed class DeepException(int frames) : Exception("deep")
    {
        public override string StackTrace { get; } =
            string.Join('\n', Enumerable.Range(0, frames).Select(i => $"   at Some.Namespace.Type{i}.Method{i}(String argument) in /src/File{i}.cs:line {i}"));
    }

    [Fact]
    public void APlainEvent_HasTheFixedFields()
    {
        var fields = Fields(new TestEvent { Level = LogLevel.Information });

        fields["MESSAGE"].ShouldBe("My.Category: it broke");
        fields["PRIORITY"].ShouldBe("6");
        fields["SYSLOG_IDENTIFIER"].ShouldBe("my-tool");
        fields["CATEGORY"].ShouldBe("My.Category");
        fields["TITLE"].ShouldBe("it broke");
        fields["CORRELATION_ID"].ShouldBe("corr-1");
        fields.Keys.ShouldBe(["MESSAGE", "PRIORITY", "SYSLOG_IDENTIFIER", "CATEGORY", "TITLE", "CORRELATION_ID"], ignoreOrder: true);
    }

    [Theory]
    [InlineData(LogLevel.Critical, "2")]
    [InlineData(LogLevel.Error, "3")]
    [InlineData(LogLevel.Warning, "4")]
    [InlineData(LogLevel.Information, "6")]
    [InlineData(LogLevel.Debug, "7")]
    [InlineData(LogLevel.Trace, "7")]
    public void ThePriority_IsTheLevelsSyslogPriority(LogLevel level, string priority) =>
        Fields(new TestEvent { Level = level })["PRIORITY"].ShouldBe(priority);

    [Fact]
    public void NoFieldIsOutsideTheList_AndNoneIsTrusted()
    {
        var events = new[]
        {
            new TestEvent { Subject = "alice", Tenant = "t1", Nesting = 1, Context = "{\"Id\":5}", Exception = TestEvent.Thrown(new InvalidOperationException("bad")) },
            new TestEvent { PayloadType = 1, Payload = "{\"a\":1}", Context = "[1]" },
            new TestEvent { PayloadType = 4, Payload = "one\ntwo", Title = new string('t', EventShaper.MaxTitleChars + 1) },
        };

        foreach (var logEvent in events)
        {
            var names = JournalMessage.Fields(Format(logEvent)).Select(f => f.Key).ToList();
            names.ShouldAllBe(name => AllowedFields.Contains(name));
            names.ShouldAllBe(name => !name.StartsWith('_'));
            names.ShouldBeUnique();
        }
    }

    [Fact]
    public void Correlation_Subject_AndTenant_AreTheirOwnFields()
    {
        var fields = Fields(new TestEvent { Subject = "alice", Tenant = "acme" });

        fields["CORRELATION_ID"].ShouldBe("corr-1");
        fields["SUBJECT"].ShouldBe("alice");
        fields["TENANT"].ShouldBe("acme");
    }

    [Fact]
    public void Correlation_Subject_AndTenant_AreLeftOut_WhenNotSet()
    {
        var fields = Fields(new TestEvent { CorrelationId = "  ", Subject = null, Tenant = "" });

        fields.ShouldNotContainKey("CORRELATION_ID");
        fields.ShouldNotContainKey("SUBJECT");
        fields.ShouldNotContainKey("TENANT");
    }

    [Theory]
    [InlineData(1)]
    [InlineData(-1)]
    public void MethodTracing_CarriesItsNesting(int nesting) =>
        Fields(new TestEvent { Level = LogLevel.Debug, Nesting = nesting })["NESTING"].ShouldBe(nesting.ToString(System.Globalization.CultureInfo.InvariantCulture));

    [Fact]
    public void NestingIsLeftOut_OnOtherEvents() =>
        Fields(new TestEvent()).ShouldNotContainKey("NESTING");

    [Fact]
    public void AnExceptionChain_IsInTheMessage_AndWholeInExceptionsJson()
    {
        var exception = TestEvent.Thrown(new InvalidOperationException("outer", TestEvent.Thrown(new ArgumentException("inner"))));

        var fields = Fields(new TestEvent { Exception = exception });

        fields["MESSAGE"].ShouldBe("My.Category: it broke | System.InvalidOperationException: outer");

        using var json = JsonDocument.Parse(fields["EXCEPTIONS_JSON"]);
        var chain = json.RootElement.EnumerateArray().ToList();
        chain.Count.ShouldBe(2);
        chain[0].GetProperty("Type").GetString().ShouldBe("System.InvalidOperationException");
        chain[0].GetProperty("Message").GetString().ShouldBe("outer");
        chain[0].GetProperty("StackTrace").GetArrayLength().ShouldBeGreaterThan(0);
        chain[1].GetProperty("Type").GetString().ShouldBe("System.ArgumentException");
        chain[1].GetProperty("Message").GetString().ShouldBe("inner");
    }

    [Fact]
    public void AContextObject_IsContextJson()
    {
        var fields = Fields(new TestEvent { Context = "{\"OrderId\":42}" });

        using var json = JsonDocument.Parse(fields["CONTEXT_JSON"]);
        json.RootElement.GetProperty("OrderId").GetInt32().ShouldBe(42);
    }

    [Theory]
    [InlineData("42")]
    [InlineData("[1,2]")]
    [InlineData("\"text\"")]
    public void AContextThatIsNotAnObject_IsWrappedAsValue(string context)
    {
        var fields = Fields(new TestEvent { Context = context });

        using var json = JsonDocument.Parse(fields["CONTEXT_JSON"]);
        json.RootElement.ValueKind.ShouldBe(JsonValueKind.Object);
        json.RootElement.GetProperty("Value").GetRawText().ShouldBe(context);
    }

    [Fact]
    public void AContext_GoesBesideAnException()
    {
        var fields = Fields(new TestEvent { Context = "{\"Id\":1}", Exception = new InvalidOperationException("bad") });

        fields.ShouldContainKey("CONTEXT_JSON");
        fields.ShouldContainKey("EXCEPTIONS_JSON");
    }

    [Fact]
    public void AJsonPayload_IsPayloadJson()
    {
        var fields = Fields(new TestEvent { PayloadType = 1, Payload = "{ \"a\": 1, \"b\": [1, 2] }" });

        fields["PAYLOAD_JSON"].ShouldBe("{\"a\":1,\"b\":[1,2]}");
        fields.ShouldNotContainKey("PAYLOAD");
    }

    [Fact]
    public void JsonThatDoesNotParse_IsPayload()
    {
        var fields = Fields(new TestEvent { PayloadType = 1, Payload = "{not json" });

        fields["PAYLOAD"].ShouldBe("{not json");
        fields.ShouldNotContainKey("PAYLOAD_JSON");
    }

    [Theory]
    [InlineData(2, "SELECT *\nFROM Orders")]   // Sql
    [InlineData(3, "<a>\n  <b/>\n</a>")]         // Xml
    [InlineData(4, "one line")]                  // Text
    [InlineData(5, "a: 1\nb: 2")]                // Yaml
    public void ATextPayload_IsPayload(int payloadType, string payload)
    {
        var fields = Fields(new TestEvent { PayloadType = payloadType, Payload = payload });

        fields["PAYLOAD"].ShouldBe(payload);
        fields.ShouldNotContainKey("PAYLOAD_JSON");
    }

    [Fact]
    public void ATextPayload_LosesCarriageReturns_AndSurroundingBlankLines() =>
        Fields(new TestEvent { PayloadType = 4, Payload = "\r\none\r\ntwo\n\n" })["PAYLOAD"].ShouldBe("one\ntwo");

    [Fact]
    public void APayload_GivesWayToAnException()
    {
        var fields = Fields(new TestEvent { PayloadType = 1, Payload = "{\"a\":1}", Exception = new InvalidOperationException("bad") });

        fields.ShouldNotContainKey("PAYLOAD_JSON");
        fields.ShouldNotContainKey("PAYLOAD");
    }

    [Fact]
    public void AValueWithANewline_IsWrittenLengthPrefixed()
    {
        var message = Format(new TestEvent { PayloadType = 4, Payload = "one\ntwo" });

        var expected = Encoding.ASCII.GetBytes("PAYLOAD\n")
            .Concat(BitConverter.IsLittleEndian ? BitConverter.GetBytes(7UL) : BitConverter.GetBytes(7UL).Reverse())
            .Concat(Encoding.UTF8.GetBytes("one\ntwo\n"))
            .ToArray();

        IndexOf(message, expected).ShouldBeGreaterThanOrEqualTo(0);
    }

    [Fact]
    public void AValueWithoutANewline_IsWrittenNameEqualsValue() =>
        IndexOf(Format(new TestEvent()), Encoding.ASCII.GetBytes("\nTITLE=it broke\n")).ShouldBeGreaterThanOrEqualTo(0);

    [Fact]
    public void BinaryUnsafeValues_ArriveWhole()
    {
        const string title = "a=b\0c\td é ✓ 𝄞\nsecond line";

        var fields = Fields(new TestEvent { Title = title, Category = "Cat\nWith.Newline" });

        fields["TITLE"].ShouldBe(title);
        fields["CATEGORY"].ShouldBe("Cat\nWith.Newline");
        fields["MESSAGE"].ShouldBe($"Cat\nWith.Newline: {title}");
    }

    [Fact]
    public void ALengthPrefix_CountsUtf8Bytes()
    {
        var message = Format(new TestEvent { PayloadType = 4, Payload = "é\n✓" });

        var at = IndexOf(message, Encoding.ASCII.GetBytes("PAYLOAD\n")) + "PAYLOAD\n".Length;
        BinaryPrimitives.ReadUInt64LittleEndian(message.AsSpan(at, 8)).ShouldBe((ulong)Encoding.UTF8.GetByteCount("é\n✓"));
    }

    [Fact]
    public void AnEventThatFits_IsNotMarkedTruncated() =>
        Fields(new TestEvent { Exception = TestEvent.Thrown(new InvalidOperationException("bad")) }).ShouldNotContainKey("TRUNCATED");

    [Fact]
    public void AFieldOverItsCap_IsCut_AndMarked()
    {
        var fields = Fields(new TestEvent { Title = new string('t', EventShaper.MaxTitleChars + 1_000) });

        fields["TITLE"].ShouldBe(new string('t', EventShaper.MaxTitleChars) + " ...[truncated]");
        fields["TRUNCATED"].ShouldBe("1");
    }

    [Fact]
    public void AnEventOver256KiB_IsCutToFit_AndMarked()
    {
        var payload = string.Join('\n', Enumerable.Range(0, 30_000).Select(i => $"line {i} of a long payload"));

        var message = Format(new TestEvent { PayloadType = 4, Payload = payload });

        message.Length.ShouldBeLessThanOrEqualTo(JournalEventWriter.MaxEventBytes);
        var fields = JournalMessage.Map(message);
        fields["TRUNCATED"].ShouldBe("1");
        fields["PAYLOAD"].ShouldStartWith("line 0 of a long payload\n");
        fields["PAYLOAD"].ShouldEndWith("...[truncated]");
    }

    [Fact]
    public void ADeepStackOver256KiB_LosesFrames_AndIsMarked()
    {
        var message = Format(new TestEvent { Exception = new DeepException(50_000) });

        message.Length.ShouldBeLessThanOrEqualTo(JournalEventWriter.MaxEventBytes);
        var fields = JournalMessage.Map(message);
        fields["TRUNCATED"].ShouldBe("1");
        using var json = JsonDocument.Parse(fields["EXCEPTIONS_JSON"]);
        json.RootElement[0].GetProperty("StackTrace").EnumerateArray().Last().GetString().ShouldContain("more frame(s) cut");
    }

    [Fact]
    public void AnEventThatCannotBeCut_IsMinimal_AndMarked()
    {
        Exception chain = null;
        for (var i = 0; i < EventShaper.MaxExceptions; i++)
            chain = new InvalidOperationException(new string('x', EventShaper.MaxExceptionMessageChars), chain);

        var message = Format(new TestEvent { Exception = chain, Context = "{\"Id\":1}" });

        message.Length.ShouldBeLessThanOrEqualTo(JournalEventWriter.MaxEventBytes);
        var fields = JournalMessage.Map(message);
        fields["TRUNCATED"].ShouldBe("1");
        fields.ShouldNotContainKey("CONTEXT_JSON");
        using var json = JsonDocument.Parse(fields["EXCEPTIONS_JSON"]);
        json.RootElement.GetArrayLength().ShouldBe(1);
    }

    [Fact]
    public void AnExceptionWhoseMessageThrows_IsStillWritten()
    {
        var fields = Fields(new TestEvent { Exception = new ThrowingException() });

        fields["MESSAGE"].ShouldContain("Message threw");
    }

    private sealed class ThrowingException : Exception
    {
        public override string Message => throw new InvalidOperationException("no message");
    }

    private static int IndexOf(byte[] haystack, byte[] needle) => haystack.AsSpan().IndexOf(needle);
}
