// Copyright (c) Pond Hawk Technologies Inc. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System.Collections;
using Microsoft.Extensions.Logging;

namespace Pondhawk.Logging.AspNetCore;

/// <summary>
/// Logs a JSON payload the way the Pondhawk.Logging API's <c>LogJson(level, title, json)</c> does — the title as the
/// message, and the payload type and text as the log-state properties a provider reads — without referencing
/// the API, which no package may do. The test project checks the event against <c>LogJson</c>'s.
/// </summary>
internal static class JsonPayload
{
    private const string PayloadTypeName = "Pondhawk.PayloadType";
    private const string PayloadContentName = "Pondhawk.PayloadContent";
    private const int Json = 1;

    public static void Log(ILogger logger, LogLevel level, string title, string? json)
    {
        if (!logger.IsEnabled(level))
            return;

        logger.Log(level, default, new State(title, json ?? string.Empty), null, State.Formatter);
    }

    private sealed class State(string title, string json) : IReadOnlyList<KeyValuePair<string, object?>>
    {
        public static readonly Func<State, Exception?, string> Formatter = static (state, _) => state.ToString();

        public int Count => 3;

        public KeyValuePair<string, object?> this[int index] => index switch
        {
            0 => new(PayloadTypeName, Json),
            1 => new(PayloadContentName, json),
            2 => new("{OriginalFormat}", title),
            _ => throw new ArgumentOutOfRangeException(nameof(index)),
        };

        public IEnumerator<KeyValuePair<string, object?>> GetEnumerator()
        {
            for (var i = 0; i < Count; i++)
                yield return this[i];
        }

        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

        public override string ToString() => title;
    }
}
