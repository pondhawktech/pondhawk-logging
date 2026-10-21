// Copyright (c) Pond Hawk Technologies Inc. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Pondhawk.Logging.Migration;

/// <summary>
/// Names a message template's holes from the expressions that fill them: the last two name segments,
/// PascalCased (<c>this.order.Id</c> → <c>OrderId</c>, <c>cause.GetType().FullName</c> → <c>CauseType</c>),
/// looking through <c>?.</c>, <c>!</c>, a cast and the left of <c>??</c>, and starting again after a call
/// (<c>x.Next().Message</c> → <c>Message</c>); a collision between different expressions takes one more leading
/// segment, then a numeric suffix; an expression with no name to use is <c>Arg0</c>, <c>Arg1</c> ….
/// The same expression twice keeps one name. Names must be unique: two holes sharing a name keep only one value
/// in the structured properties.
/// </summary>
internal static class HoleNames
{
    private const int Taken = 2;

    /// <summary>A name for each expression, in order.</summary>
    public static IReadOnlyList<string> For(IReadOnlyList<ExpressionSyntax> expressions, SemanticModel? model = null)
    {
        var texts = expressions.Select(e => e.ToString()).ToList();
        var segments = expressions.Select(e => Segments(e, bound: null, model)).ToList();
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

    // order.Id → [order, Id]; this.total → [total]; row?.Status → [row, Status]; name! and name ?? "none" → [name];
    // cause.GetType().FullName → [cause, Type]; a member after a call starts again (x.Next().Message → [Message]);
    // Safe(runUid) → [runUid] when it returns its argument's type; ok ? reason : "none" → [reason];
    // nothing to name it by (a literal, any other call, arithmetic) → null.
    // bound: the segments of the receiver a ?. binds its members to. model, when there is one, tells a call that
    // passes its one argument through (Safe(runUid), Trim(name)) from one that looks something up (PathOf(context)).
    private static List<string>? Segments(ExpressionSyntax expression, List<string>? bound, SemanticModel? model)
    {
        switch (expression)
        {
            case IdentifierNameSyntax name:
                return [name.Identifier.ValueText];
            case MemberAccessExpressionSyntax { Expression: ThisExpressionSyntax or BaseExpressionSyntax } access:
                return [access.Name.Identifier.ValueText];
            case MemberAccessExpressionSyntax { Name.Identifier.ValueText: "FullName" or "Name" } access when IsGetType(access.Expression):
                return Segments(access.Expression, bound, model);
            case MemberAccessExpressionSyntax access:
                var left = Segments(access.Expression, bound, model) ?? [];
                left.Add(access.Name.Identifier.ValueText);
                return left;
            case MemberBindingExpressionSyntax binding when bound is not null:
                return [.. bound, binding.Name.Identifier.ValueText];
            case ConditionalAccessExpressionSyntax conditional:
                return Segments(conditional.WhenNotNull, Segments(conditional.Expression, bound, model) ?? [], model);
            case InvocationExpressionSyntax invocation when IsGetType(invocation):
                var receiver = invocation.Expression is MemberAccessExpressionSyntax { Expression: not (ThisExpressionSyntax or BaseExpressionSyntax) } target
                    ? Segments(target.Expression, bound, model) ?? []
                    : invocation.Expression is MemberBindingExpressionSyntax ? [.. bound ?? []] : [];
                receiver.Add("Type");
                return receiver;
            case InvocationExpressionSyntax { ArgumentList.Arguments: [var only] } call when PassesThrough(call, only.Expression, model):
                return Segments(only.Expression, bound, model);
            case ConditionalExpressionSyntax choice:
                return Segments(choice.WhenTrue, bound, model) ?? Segments(choice.WhenFalse, bound, model);
            case PostfixUnaryExpressionSyntax postfix when postfix.IsKind(SyntaxKind.SuppressNullableWarningExpression):
                return Segments(postfix.Operand, bound, model);
            case BinaryExpressionSyntax coalesce when coalesce.IsKind(SyntaxKind.CoalesceExpression):
                return Segments(coalesce.Left, bound, model);
            case CastExpressionSyntax cast:
                return Segments(cast.Expression, bound, model);
            case ParenthesizedExpressionSyntax parenthesized:
                return Segments(parenthesized.Expression, bound, model);
            default:
                return null;
        }
    }

    // A call returning the type of its one argument: a wrapper that cleans or guards a value, not one that looks
    // something up.
    private static bool PassesThrough(InvocationExpressionSyntax call, ExpressionSyntax argument, SemanticModel? model) =>
        model is not null
        && call.SyntaxTree == model.SyntaxTree
        && model.GetTypeInfo(call).Type is { TypeKind: not TypeKind.Error } returned
        && SymbolEqualityComparer.Default.Equals(returned, model.GetTypeInfo(argument).Type);

    // x.GetType(), GetType(), x?.GetType() — and the same with a trailing ! — with no arguments.
    private static bool IsGetType(ExpressionSyntax expression)
    {
        while (expression is PostfixUnaryExpressionSyntax postfix && postfix.IsKind(SyntaxKind.SuppressNullableWarningExpression))
            expression = postfix.Operand;

        return expression is InvocationExpressionSyntax { ArgumentList.Arguments.Count: 0 } invocation
            && invocation.Expression switch
            {
                IdentifierNameSyntax name => string.Equals(name.Identifier.ValueText, "GetType", StringComparison.Ordinal),
                MemberAccessExpressionSyntax access => string.Equals(access.Name.Identifier.ValueText, "GetType", StringComparison.Ordinal),
                MemberBindingExpressionSyntax binding => string.Equals(binding.Name.Identifier.ValueText, "GetType", StringComparison.Ordinal),
                _ => false,
            };
    }

    private static string Pascal(string name)
    {
        var trimmed = name.TrimStart('_', '@');
        return trimmed.Length == 0 ? "Value" : char.ToUpperInvariant(trimmed[0]) + trimmed.Substring(1);
    }
}
