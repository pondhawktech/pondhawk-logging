// Copyright (c) Pond Hawk Technologies Inc. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;

namespace Pondhawk.Logging.Migration;

/// <summary>
/// The message arguments a level call becomes — the template and its values. Names and meaning come from the
/// original call; the text written for an argument of the call is its current text, so rewrites inside it
/// survive. What cannot be converted is formatted as Fabrica.Watch did and marked with a TODO.
/// </summary>
internal sealed class Messages(SemanticModel model, InvocationExpressionSyntax original, InvocationExpressionSyntax current, RewriteContext context)
{
    /// <summary>The arguments after the exception for <paramref name="call"/>.</summary>
    public List<string> For(LevelCall call) =>
        call.Shape switch
        {
            LevelShape.Template => Template(call),
            LevelShape.Parts => Parts(call.Message!),
            LevelShape.Lazy => Lazy(call.Message),
            _ => Simple(call.MessageArgument, call.Kind, call.Exception),
        };

    /// <summary>A plain message: a constant stays the template; anything else fills a hole.</summary>
    public List<string> Simple(IArgumentOperation? argument, MessageKind kind, ExpressionSyntax? exception)
    {
        var message = Current(FabricaWatch.Written(argument));
        return kind switch
        {
            MessageKind.Constant => [message!],
            MessageKind.Text => ["\"{Message}\"", message!],
            MessageKind.Value => ["\"{Value}\"", message!],
            _ when exception is IdentifierNameSyntax => ["\"{Message}\"", Current(exception) + ".Message"],
            _ => ["\"\""],
        };
    }

    /// <summary>Text for an expression the original call wrote as one of its arguments.</summary>
    public string? Current(ExpressionSyntax? written)
    {
        if (written?.Parent is not ArgumentSyntax argument)
            return written?.ToString();

        var index = original.ArgumentList.Arguments.IndexOf(argument);
        return index < 0 || index >= current.ArgumentList.Arguments.Count ? written.ToString() : current.ArgumentList.Arguments[index].Expression.ToString();
    }

    // "Placed {0} for {1}", id, name → "Placed {Id} for {Name}", id, name
    private List<string> Template(LevelCall call)
    {
        var values = call.Arguments is { ArgumentKind: ArgumentKind.ParamArray, Value: IArrayCreationOperation { Initializer: { } initializer } }
            ? initializer.ElementValues.Select(v => v.Syntax as ExpressionSyntax).ToList()
            : null;

        var format = model.GetConstantValue(call.Message!).Value as string;
        var template = format is null || values is null || values.Any(v => v is null)
            ? null
            : MessageTemplate.FromCompositeFormat(format, values!);

        if (template is null)
            return Formatted(call);

        foreach (var repeated in template.Values.GroupBy(v => v).Where(g => g.Count() > 1 && !IsCheap(g.Key)))
            context.Todo($"'{repeated.Key}' fills {repeated.Count()} holes and is now evaluated {repeated.Count()} times");

        return [template.ToLiteral(), .. template.Values.Select(v => Current(v)!)];
    }

    // The template could not be read: format it as Fabrica.Watch did.
    private List<string> Formatted(LevelCall call)
    {
        context.Todo("the template could not be turned into a message template, so it is formatted as before; give it named holes");

        var arguments = call.Arguments is { ArgumentKind: ArgumentKind.ParamArray, Value: IArrayCreationOperation { Initializer: { } initializer } }
            ? initializer.ElementValues.Select(v => Current(v.Syntax as ExpressionSyntax)!)
            : [Current(FabricaWatch.Written(call.Arguments))!];

        return ["\"{Message}\"", "string.Format(" + string.Join(", ", new[] { Current(call.Message)! }.Concat(arguments)) + ")"];
    }

    // $"Placed {id}" or "Placed " + id → "Placed {Id}", id
    private List<string> Parts(ExpressionSyntax message)
    {
        var template = new MessageTemplate();
        AddParts(template, message);
        return [template.ToLiteral(), .. template.Values.Select(v => v.ToString())];
    }

    private void AddParts(MessageTemplate template, ExpressionSyntax expression)
    {
        switch (expression)
        {
            case ParenthesizedExpressionSyntax parenthesized when IsString(parenthesized):
                AddParts(template, parenthesized.Expression);
                break;

            case InterpolatedStringExpressionSyntax interpolated:
                // A regular interpolated string keeps its braces doubled, as a template does; a raw one has them single.
                var raw = interpolated.StringStartToken.Text.Contains("\"\"\"");
                foreach (var content in interpolated.Contents)
                {
                    if (content is InterpolatedStringTextSyntax text && raw)
                        template.AddText(text.TextToken.ValueText);
                    else if (content is InterpolatedStringTextSyntax escaped)
                        template.AddEscapedText(escaped.TextToken.ValueText);
                    else if (content is InterpolationSyntax hole)
                        template.AddHole(hole.Expression, hole.AlignmentClause?.ToString() + hole.FormatClause?.ToString());
                }

                break;

            case BinaryExpressionSyntax binary when binary.IsKind(SyntaxKind.AddExpression) && IsString(binary):
                AddParts(template, binary.Left);
                AddParts(template, binary.Right);
                break;

            default:
                if (model.GetConstantValue(expression).Value is string constant)
                    template.AddText(constant);
                else
                    template.AddHole(expression, string.Empty);
                break;
        }
    }

    // () => $"Placed {id}" → "Placed {Id}", id — the guard that keeps it lazy is the caller's.
    private List<string> Lazy(ExpressionSyntax? message)
    {
        if (message is LambdaExpressionSyntax { ExpressionBody: { } body })
        {
            if (body is InterpolatedStringExpressionSyntax || (body is BinaryExpressionSyntax && IsString(body) && !model.GetConstantValue(body).HasValue))
                return Parts(body);

            return model.GetConstantValue(body).Value is string ? [body.ToString()] : ["\"{Message}\"", body.ToString()];
        }

        var text = Current(message);
        return message is AnonymousFunctionExpressionSyntax
            ? ["\"{Message}\"", "new System.Func<string>(" + text + ")()"]
            : ["\"{Message}\"", text + "()"];
    }

    private bool IsString(ExpressionSyntax expression) =>
        model.GetTypeInfo(expression).Type?.SpecialType == SpecialType.System_String;

    private static bool IsCheap(ExpressionSyntax expression) =>
        expression is IdentifierNameSyntax or LiteralExpressionSyntax
        || (expression is MemberAccessExpressionSyntax access && IsCheap(access.Expression))
        || expression is ThisExpressionSyntax;
}
