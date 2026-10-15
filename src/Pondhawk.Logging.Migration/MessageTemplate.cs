// Copyright (c) Pond Hawk Technologies Inc. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Pondhawk.Logging.Migration;

/// <summary>
/// A Microsoft.Extensions.Logging message template being built: literal text and holes, each hole filled by an
/// expression. Holes are matched to values by position, so a value used twice is passed twice.
/// </summary>
internal sealed class MessageTemplate
{
    private readonly List<object> _parts = [];   // string (literal text, braces already escaped) or Hole

    /// <summary>The expressions filling the holes, in order, as the original wrote them.</summary>
    public List<ExpressionSyntax> Values { get; } = [];

    /// <summary>Literal text, escaped for a template (<c>{</c> → <c>{{</c>).</summary>
    public void AddText(string text) => _parts.Add(text.Replace("{", "{{").Replace("}", "}}"));

    /// <summary>Text already escaped as a template's is, as .NET composite formats are.</summary>
    public void AddEscapedText(string text) => _parts.Add(text);

    /// <summary>A hole for <paramref name="value"/>, with its alignment and format (<c>,-10:N2</c>), if any.</summary>
    public void AddHole(ExpressionSyntax value, string suffix)
    {
        _parts.Add(new Hole(Values.Count, suffix));
        Values.Add(value);
    }

    /// <summary>The template as a C# string literal, its holes named from their values.</summary>
    public string ToLiteral()
    {
        var names = HoleNames.For(Values);
        var text = new StringBuilder();
        foreach (var part in _parts)
            text.Append(part is Hole hole ? "{" + names[hole.Index] + hole.Suffix + "}" : (string)part);

        return SyntaxFactory.Literal(text.ToString()).Text;
    }

    /// <summary>
    /// Converts a .NET composite format (<c>"Placed {0} at {1:t}"</c>) given the argument expressions, or returns
    /// null when the format is malformed or names an argument that is not there.
    /// </summary>
    public static MessageTemplate? FromCompositeFormat(string format, IReadOnlyList<ExpressionSyntax> arguments)
    {
        var template = new MessageTemplate();
        var literal = new StringBuilder();

        for (var i = 0; i < format.Length; i++)
        {
            var c = format[i];
            if ((c == '{' || c == '}') && i + 1 < format.Length && format[i + 1] == c)
            {
                literal.Append(c, 2);
                i++;
                continue;
            }

            if (c == '}')
                return null;

            if (c != '{')
            {
                literal.Append(c);
                continue;
            }

            var end = format.IndexOf('}', i + 1);
            if (end < 0 || !TryHole(format.Substring(i + 1, end - i - 1), arguments.Count, out var index, out var suffix))
                return null;

            template.AddEscapedText(literal.ToString());
            literal.Clear();
            template.AddHole(arguments[index], suffix);
            i = end;
        }

        template.AddEscapedText(literal.ToString());
        return template;
    }

    // "0", "1,-10", "2:N2", "3,8:yyyy-MM-dd" — an index below the argument count, and what follows it.
    private static bool TryHole(string content, int count, out int index, out string suffix)
    {
        index = -1;
        var digits = 0;
        while (digits < content.Length && char.IsDigit(content[digits]))
            digits++;

        suffix = content.Substring(digits).Trim();
        var valid = digits > 0
            && int.TryParse(content.Substring(0, digits), NumberStyles.None, CultureInfo.InvariantCulture, out index)
            && index < count
            && content.IndexOf('{') < 0
            && (suffix.Length == 0 || suffix[0] == ',' || suffix[0] == ':');

        if (!valid)
            index = -1;

        return valid;
    }

    private sealed record Hole(int Index, string Suffix);
}
