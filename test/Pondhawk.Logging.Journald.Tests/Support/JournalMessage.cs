// Copyright (c) Pond Hawk Technologies Inc. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System.Buffers.Binary;
using System.Text;

namespace Pondhawk.Logging.Journald.Tests.Support;

/// <summary>Reads a journald native-protocol message back into its fields, as journald does.</summary>
public static class JournalMessage
{
    public static IReadOnlyList<KeyValuePair<string, string>> Fields(byte[] message)
    {
        var fields = new List<KeyValuePair<string, string>>();
        var at = 0;
        while (at < message.Length)
        {
            var end = Array.IndexOf(message, (byte)'\n', at);
            if (end < 0)
                throw new FormatException($"Field at {at} has no terminating newline.");

            var equals = Array.IndexOf(message, (byte)'=', at, end - at);
            if (equals >= 0)
            {
                fields.Add(new(Ascii(message, at, equals), Encoding.UTF8.GetString(message, equals + 1, end - equals - 1)));
                at = end + 1;
                continue;
            }

            // NAME\n, 64-bit little-endian length, value, \n.
            var name = Ascii(message, at, end);
            var length = checked((int)BinaryPrimitives.ReadUInt64LittleEndian(message.AsSpan(end + 1, sizeof(ulong))));
            var start = end + 1 + sizeof(ulong);
            if (message[start + length] != (byte)'\n')
                throw new FormatException($"Field {name} is not followed by a newline.");

            fields.Add(new(name, Encoding.UTF8.GetString(message, start, length)));
            at = start + length + 1;
        }

        return fields;
    }

    public static Dictionary<string, string> Map(byte[] message) =>
        Fields(message).ToDictionary(f => f.Key, f => f.Value, StringComparer.Ordinal);

    private static string Ascii(byte[] message, int from, int to) => Encoding.ASCII.GetString(message, from, to - from);
}
