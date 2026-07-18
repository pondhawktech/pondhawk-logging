// Copyright (c) Pond Hawk Technologies Inc. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System.Collections;
using Microsoft.Extensions.Logging;

namespace Pondhawk.Logging;

/// <summary>
/// The log state the Pondhawk logging extensions attach to a <see cref="ILogger.Log"/> call: a rendered
/// title plus a set of well-known control properties (see <see cref="LogPropertyNames"/>). It is a plain
/// <see cref="IReadOnlyList{T}"/> of key/value pairs — the same shape <c>Microsoft.Extensions.Logging</c>
/// uses for structured state — so any provider can read it, and a sink (e.g. the Watch provider) can pull
/// the control properties back out by key. The final element is the conventional <c>{OriginalFormat}</c>
/// entry carrying the title.
/// </summary>
internal sealed class LogState : IReadOnlyList<KeyValuePair<string, object?>>
{
    /// <summary>The message formatter passed to <see cref="ILogger.Log"/>; renders the title.</summary>
    public static readonly Func<LogState, Exception?, string> Formatter = static (state, _) => state._title;

    private const string OriginalFormatKey = "{OriginalFormat}";

    private readonly string _title;
    private readonly KeyValuePair<string, object?>[] _properties;

    /// <summary>Creates a state with the given rendered <paramref name="title"/> and control properties.</summary>
    /// <param name="title">The rendered message title.</param>
    /// <param name="properties">The control properties (payload type/content, nesting, …).</param>
    public LogState(string title, params KeyValuePair<string, object?>[] properties)
    {
        _title = title;
        _properties = properties;
    }

    /// <inheritdoc />
    public int Count => _properties.Length + 1;

    /// <inheritdoc />
    public KeyValuePair<string, object?> this[int index]
        => index < _properties.Length
            ? _properties[index]
            : new KeyValuePair<string, object?>(OriginalFormatKey, _title);

    /// <inheritdoc />
    public IEnumerator<KeyValuePair<string, object?>> GetEnumerator()
    {
        for (var i = 0; i < Count; i++)
            yield return this[i];
    }

    IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();

    /// <summary>Returns the rendered title.</summary>
    public override string ToString() => _title;
}
