// Copyright (c) Pond Hawk Technologies Inc. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Pondhawk.Logging.Migration;

/// <summary>
/// Names a message template's holes from the expressions that fill them: the last two name segments,
/// PascalCased (<c>this.order.Id</c> → <c>OrderId</c>); a collision between different expressions takes one more
/// leading segment, then a numeric suffix; an expression with no name to use is <c>Arg0</c>, <c>Arg1</c> ….
/// The same expression twice keeps one name. Names must be unique: two holes sharing a name keep only one value
/// in the structured properties.
/// </summary>
internal static class HoleNames
{
    private const int Taken = 2;

    /// <summary>A name for each expression, in order.</summary>
    public static IReadOnlyList<string> For(IReadOnlyList<ExpressionSyntax> expressions)
    {
        var texts = expressions.Select(e => e.ToString()).ToList();
        var segments = expressions.Select(Segments).ToList();
        var widths = segments.Select(s => s is null ? 0 : Math.Min(Taken, s.Count)).ToList();
        var first = Enumerable.Range(0, expressions.Count).Select(i => texts.IndexOf(texts[i])).ToList();
        var names = Enumerable.Range(0, expressions.Count).Select(i => NameOf(segments[i], widths[i], first[i])).ToList();

        // Widen where different expressions share a name, while they have segments to give.
        for (var widened = true; widened;)
        {
            widened = false;
            foreach (var i in Clashing(names, texts))
            {
                if (segments[i] is { } s && widths[i] < s.Count)
                {
                    widths[i]++;
                    names[i] = NameOf(s, widths[i], first[i]);
                    widened = true;
                }
            }
        }

        return Numbered(names, texts);
    }

    // The indexes whose name another, different expression also has.
    private static List<int> Clashing(List<string> names, List<string> texts) =>
        Enumerable.Range(0, names.Count)
            .Where(i => Enumerable.Range(0, names.Count).Any(j => j != i
                && string.Equals(names[j], names[i], StringComparison.Ordinal)
                && !string.Equals(texts[j], texts[i], StringComparison.Ordinal)))
            .ToList();

    // Collisions left after widening: the first keeps the name, the next take 2, 3 ….
    private static List<string> Numbered(List<string> names, List<string> texts)
    {
        var owner = new Dictionary<string, string>(StringComparer.Ordinal);
        var result = new List<string>(names.Count);

        for (var i = 0; i < names.Count; i++)
        {
            var name = names[i];
            for (var n = 2; owner.TryGetValue(name, out var text) && !string.Equals(text, texts[i], StringComparison.Ordinal); n++)
                name = names[i] + n.ToString(CultureInfo.InvariantCulture);

            owner[name] = texts[i];
            result.Add(name);
        }

        return result;
    }

    private static string NameOf(List<string>? segments, int width, int index) =>
        segments is null || width == 0
            ? "Arg" + index.ToString(CultureInfo.InvariantCulture)
            : string.Concat(segments.Skip(segments.Count - width).Select(Pascal));

    // order.Id → [order, Id]; this.total → [total]; anything that is not a chain of names → null.
    private static List<string>? Segments(ExpressionSyntax expression)
    {
        switch (expression)
        {
            case IdentifierNameSyntax name:
                return [name.Identifier.ValueText];
            case MemberAccessExpressionSyntax { Expression: ThisExpressionSyntax or BaseExpressionSyntax } access:
                return [access.Name.Identifier.ValueText];
            case MemberAccessExpressionSyntax access when Segments(access.Expression) is { } left:
                left.Add(access.Name.Identifier.ValueText);
                return left;
            case ParenthesizedExpressionSyntax parenthesized:
                return Segments(parenthesized.Expression);
            default:
                return null;
        }
    }

    private static string Pascal(string name)
    {
        var trimmed = name.TrimStart('_', '@');
        return trimmed.Length == 0 ? "Value" : char.ToUpperInvariant(trimmed[0]) + trimmed.Substring(1);
    }
}
