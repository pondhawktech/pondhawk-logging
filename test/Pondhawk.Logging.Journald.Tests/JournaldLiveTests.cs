// Copyright (c) Pond Hawk Technologies Inc. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System.Diagnostics;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using Pondhawk.Context;
using Pondhawk.Logging.Journald.Tests.Support;
using Shouldly;
using Xunit;

namespace Pondhawk.Logging.Journald.Tests;

/// <summary>
/// Runs only when <c>JOURNALD_LIVE</c> is set, so a plain <c>dotnet test</c> skips it:
/// <c>JOURNALD_LIVE=1 dotnet test --filter Category=Journald</c>. <c>JOURNALD_LIVE_JOURNALCTL</c> names the
/// command that reads the journal back, <c>journalctl</c> by default — <c>sudo journalctl</c> where the user
/// cannot read the system journal, as on a CI runner.
/// </summary>
public sealed class JournaldLiveFactAttribute : FactAttribute
{
    public const string LiveVariable = "JOURNALD_LIVE";
    public const string JournalctlVariable = "JOURNALD_LIVE_JOURNALCTL";

    public JournaldLiveFactAttribute()
    {
        if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(LiveVariable)))
            Skip = $"Set {LiveVariable}=1 to run against the real journald.";
    }
}

/// <summary>
/// The sink against the real journald: each event sent to its socket and read back with
/// <c>journalctl -o json</c>, every field compared with what was sent. Each test writes under its own
/// <c>SYSLOG_IDENTIFIER</c> and reads back only that.
/// </summary>
[Trait("Category", "Journald")]
public class JournaldLiveTests
{
    private static string NewIdentifier() => "jd-live-" + Guid.NewGuid().ToString("N")[..12];

    private sealed class DeepException(int frames) : Exception("deep")
    {
        public override string StackTrace { get; } =
            string.Join('\n', Enumerable.Range(0, frames).Select(i => $"   at Some.Namespace.Type{i}.Method{i}(String argument) in /src/File{i}.cs:line {i}"));
    }

    [JournaldLiveFact]
    public async Task EveryField_ArrivesAsSent()
    {
        var identifier = NewIdentifier();
        var events = new[]
        {
            new TestEvent { Level = LogLevel.Information, Title = "plain" },
            new TestEvent { Title = "who", Subject = "alice", Tenant = "acme" },
            new TestEvent { Level = LogLevel.Debug, Title = "enter", Nesting = 1 },
            new TestEvent { Title = "chain", Context = "{\"OrderId\":42}", Exception = TestEvent.Thrown(new InvalidOperationException("outer", TestEvent.Thrown(new ArgumentException("inner")))) },
            new TestEvent { Title = "json", PayloadType = 1, Payload = "{\"a\":1,\"b\":[1,2]}", Context = "7" },
            new TestEvent { Title = "sql", PayloadType = 2, Payload = "SELECT *\nFROM Orders\nWHERE Id = 1" },
            new TestEvent { Title = "first line\nsecond line", Category = "Unicode.é✓" },
            new TestEvent { Title = "capped " + new string('t', 20_000) },
        };

        var sent = new List<Dictionary<string, string>>();
        using (var socket = JournalSocket.TryOpen())
        {
            socket.ShouldNotBeNull();
            foreach (var logEvent in events)
            {
                var message = JournalEventWriter.Format(logEvent.Journald(), identifier);
                socket.TrySend(message).ShouldBeTrue();
                sent.Add(JournalMessage.Map(message));
            }
        }

        var received = await Read(identifier, events.Length);

        received.Count.ShouldBe(sent.Count);
        for (var i = 0; i < sent.Count; i++)
            ShouldMatch(received[i], sent[i], $"event {i}");
    }

    [JournaldLiveFact]
    public async Task AnEventLargerThanADatagram_ArrivesWhole_ByMemfd()
    {
        var identifier = NewIdentifier();
        var payload = string.Join('\n', Enumerable.Range(0, 5_000).Select(i => $"line {i} of a payload larger than one datagram"));
        var message = JournalEventWriter.Format(new TestEvent { Title = "large", PayloadType = 4, Payload = payload }.Journald(), identifier);

        using (var socket = JournalSocket.TryOpen(sendBufferBytes: 16 * 1024))
            socket.TrySend(message).ShouldBeTrue();

        var received = (await Read(identifier, 1)).Single();
        ShouldMatch(received, JournalMessage.Map(message), "event");
        received["PAYLOAD"].Length.ShouldBe(payload.Length);
    }

    [JournaldLiveFact]
    public async Task AnEventOver256KiB_ArrivesCut_AndMarked()
    {
        var identifier = NewIdentifier();
        var message = JournalEventWriter.Format(new TestEvent { Title = "huge", Exception = new DeepException(50_000) }.Journald(), identifier);
        message.Length.ShouldBeLessThanOrEqualTo(JournalEventWriter.MaxEventBytes);

        using (var socket = JournalSocket.TryOpen())
            socket.TrySend(message).ShouldBeTrue();

        var received = (await Read(identifier, 1)).Single();
        ShouldMatch(received, JournalMessage.Map(message), "event");
        received["TRUNCATED"].ShouldBe("1");
    }

    [JournaldLiveFact]
    public async Task ThroughAddJournald_TheLogStateArrivesAsFields()
    {
        var identifier = NewIdentifier();
        var factory = LoggerFactory.Create(builder => builder.AddJournald(
            LogLevel.Debug, o => o.SyslogIdentifier = identifier, journal: () => JournalSocket.TryOpen(), stdout: null));
        var logger = factory.CreateLogger("Pondhawk.Logging.Live");

        logger.LogTrace("below the floor");
        logger.LogInformation("Order {OrderId} placed", 42);
        using (Operation.Begin("01KLIVE"))
        {
            Ambient<IOperationContext>.Current.SubjectName = "kchen";
            Ambient<IOperationContext>.Current.Tenant = "acme";
            logger.LogJson(LogLevel.Debug, "request", "{\"a\":1}");
            logger.ErrorWithContext(new InvalidOperationException("live"), new { OrderId = 42 }, "live test");
        }

        // Nothing to flush: each event was sent inside its call. Read back before disposing to show it.
        var received = await Read(identifier, 3);
        factory.Dispose();

        received.Select(e => e["TITLE"]).ShouldBe(["Order 42 placed", "request", "live test"]);
        received.ShouldAllBe(e => e["CATEGORY"] == "Pondhawk.Logging.Live" && e["SYSLOG_IDENTIFIER"] == identifier);

        received[0]["MESSAGE"].ShouldBe("Pondhawk.Logging.Live: Order 42 placed");
        received[0]["PRIORITY"].ShouldBe("6");

        received[1]["PRIORITY"].ShouldBe("7");
        received[1]["PAYLOAD_JSON"].ShouldBe("{\"a\":1}");
        received[1]["CORRELATION_ID"].ShouldBe("01KLIVE");
        received[1]["SUBJECT"].ShouldBe("kchen");
        received[1]["TENANT"].ShouldBe("acme");

        received[2]["PRIORITY"].ShouldBe("3");
        received[2]["MESSAGE"].ShouldBe("Pondhawk.Logging.Live: live test | System.InvalidOperationException: live");
        using var context = JsonDocument.Parse(received[2]["CONTEXT_JSON"]);
        context.RootElement.GetProperty("OrderId").GetInt32().ShouldBe(42);
        using var exceptions = JsonDocument.Parse(received[2]["EXCEPTIONS_JSON"]);
        exceptions.RootElement[0].GetProperty("Message").GetString().ShouldBe("live");
    }

    // The fields that differ, by name, rather than both entries whole: a value here can be 256 KiB.
    private static void ShouldMatch(Dictionary<string, string> received, Dictionary<string, string> sent, string what)
    {
        var differences = sent.Keys.Union(received.Keys)
            .Where(name => !received.TryGetValue(name, out var r) || !sent.TryGetValue(name, out var s) || r != s)
            .Select(name => $"{name}: sent {Describe(sent, name)}, received {Describe(received, name)}")
            .ToList();

        differences.ShouldBeEmpty($"{what}:{Environment.NewLine}{string.Join(Environment.NewLine, differences)}");
    }

    private static string Describe(Dictionary<string, string> fields, string name)
    {
        if (!fields.TryGetValue(name, out var value))
            return "nothing";

        return value.Length <= 80 ? $"\"{value}\"" : $"{value.Length} chars ending \"{value[^40..]}\"";
    }

    /// <summary>
    /// The entries journald stored under <paramref name="identifier"/>, oldest first, each as its fields
    /// without journald's own (<c>_</c> and <c>__</c>). Waits up to ten seconds for <paramref name="expected"/>.
    /// </summary>
    private static async Task<List<Dictionary<string, string>>> Read(string identifier, int expected)
    {
        var entries = new List<Dictionary<string, string>>();
        for (var attempt = 0; attempt < 20 && entries.Count < expected; attempt++)
        {
            if (attempt > 0)
                await Task.Delay(500);
            entries = await Journalctl(identifier);
        }

        return entries;
    }

    private static async Task<List<Dictionary<string, string>>> Journalctl(string identifier)
    {
        var command = (Environment.GetEnvironmentVariable(JournaldLiveFactAttribute.JournalctlVariable) ?? "journalctl")
            .Split(' ', StringSplitOptions.RemoveEmptyEntries);

        // --all: without it, journalctl writes null for any value over 4096 bytes.
        var start = new ProcessStartInfo(command[0]) { RedirectStandardOutput = true, RedirectStandardError = true };
        foreach (var argument in command.Skip(1).Concat(["--quiet", "--output=json", "--all", $"SYSLOG_IDENTIFIER={identifier}"]))
            start.ArgumentList.Add(argument);

        using var journalctl = Process.Start(start)!;
        var output = await journalctl.StandardOutput.ReadToEndAsync();
        var error = await journalctl.StandardError.ReadToEndAsync();
        await journalctl.WaitForExitAsync();
        journalctl.ExitCode.ShouldBe(0, error);

        return output.Split('\n', StringSplitOptions.RemoveEmptyEntries).Select(Fields).ToList();
    }

    // journalctl writes a value that is not printable UTF-8 as an array of its bytes.
    private static Dictionary<string, string> Fields(string line)
    {
        using var entry = JsonDocument.Parse(line);
        return entry.RootElement.EnumerateObject()
            .Where(p => !p.Name.StartsWith('_'))
            .ToDictionary(
                p => p.Name,
                p => p.Value.ValueKind == JsonValueKind.Array
                    ? Encoding.UTF8.GetString(p.Value.EnumerateArray().Select(b => b.GetByte()).ToArray())
                    : p.Value.GetString(),
                StringComparer.Ordinal);
    }
}
