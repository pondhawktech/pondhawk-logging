// Copyright (c) Pond Hawk Technologies Inc. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System;
using System.Collections.Generic;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Formatting;

namespace Pondhawk.Logging.Migration;

/// <summary>
/// Rewrites a function body's method and scope tracing (PHLM006, PHLM007). Pondhawk.Logging ends a method's
/// trace when its <c>EnterMethod</c> is disposed, so <c>LeaveMethod</c> goes, the <c>EnterMethod</c> becomes a
/// <c>using</c> declaration, and a <c>try/finally</c> that only closed the trace goes with it.
/// <c>EnterScope/LeaveScope</c> become a <c>using (logger.EnterMethod(name)) { … }</c> around what they bracketed.
/// Matching is by the logger's text, among the loggers the original body traced with Fabrica.Watch.
/// </summary>
internal sealed class Tracing(HashSet<string> loggers, HashSet<string> undisposed, RewriteContext context) : CSharpSyntaxRewriter
{
    private readonly HashSet<string> _left = new(StringComparer.Ordinal);
    private readonly HashSet<string> _leaving = new(StringComparer.Ordinal);
    private readonly HashSet<string> _entered = new(StringComparer.Ordinal);

    /// <summary>Rewrites <paramref name="body"/>, leaving TODOs above its first statement for what it could not settle.</summary>
    public BlockSyntax Rewrite(BlockSyntax body)
    {
        // Only an EnterMethod a LeaveMethod closes in this body becomes a using declaration.
        foreach (var statement in body.DescendantNodes().OfType<StatementSyntax>())
        {
            if (Call(statement) is { Method: "LeaveMethod" } leave)
                _leaving.Add(leave.Logger);
        }

        var rewritten = (BlockSyntax)Visit(body);

        foreach (var orphan in _left.Except(_entered, StringComparer.Ordinal))
            context.Todo($"'{orphan}.LeaveMethod()' was removed, but no EnterMethod on '{orphan}' was found to make a using declaration of; check its method trace");

        if (rewritten.Statements.Count == 0)
            return rewritten;

        return rewritten.ReplaceNode(rewritten.Statements[0], context.Mark(rewritten.Statements[0]));
    }

    /// <inheritdoc />
    public override SyntaxNode? VisitExpressionStatement(ExpressionStatementSyntax node)
    {
        if (undisposed.Contains(node.Expression.ToString()))
            return Removed(node);

        if (Call(node) is not { } call)
            return base.VisitExpressionStatement(node);

        switch (call.Method)
        {
            case "LeaveMethod":
                _left.Add(call.Logger);
                return Removed(node);

            case "EnterMethod" when _leaving.Contains(call.Logger):
                _entered.Add(call.Logger);
                return UsingDeclaration("_", call.Invocation.WithoutTrivia(), node);

            default:
                return base.VisitExpressionStatement(node);
        }
    }

    /// <inheritdoc />
    public override SyntaxNode? VisitUsingStatement(UsingStatementSyntax node)
    {
        // using (var logger = this.GetLogger()) { … } → { var logger = this.GetLogger(); … }
        if (node.Declaration is not { Variables.Count: 1 } declaration || !undisposed.Contains(declaration.Variables[0].Identifier.ValueText))
            return base.VisitUsingStatement(node);

        var inner = (StatementSyntax)Visit(node.Statement)!;
        var statements = new List<StatementSyntax> { SyntaxFactory.LocalDeclarationStatement(declaration.WithoutTrivia()) };
        statements.AddRange(inner is BlockSyntax block ? block.Statements : [inner]);

        return SyntaxFactory.Block(statements).WithTriviaFrom(node).WithAdditionalAnnotations(Formatter.Annotation);
    }

    /// <inheritdoc />
    public override SyntaxNode? VisitBlock(BlockSyntax node)
    {
        var block = (BlockSyntax)base.VisitBlock(node)!;
        var statements = new List<StatementSyntax>();

        foreach (var statement in Scopes(block.Statements))
        {
            switch (statement)
            {
                case TryStatementSyntax { Finally.Block.Statements.Count: 0 } attempt when attempt.Catches.Count == 0:
                    statements.AddRange(attempt.Block.Statements.Select(s => s.WithAdditionalAnnotations(Formatter.Annotation)));
                    break;

                case TryStatementSyntax { Finally.Block.Statements.Count: 0 } attempt:
                    statements.Add(attempt.WithFinally(null));
                    break;

                case LocalDeclarationStatementSyntax { Declaration.Variables.Count: 1 } declaration
                    when !declaration.UsingKeyword.IsKind(SyntaxKind.None) && undisposed.Contains(declaration.Declaration.Variables[0].Identifier.ValueText):
                    statements.Add(WithoutUsing(declaration));
                    break;

                case LocalDeclarationStatementSyntax declaration when EnteredLocal(declaration) is { } name:
                    _entered.Add(name);
                    statements.Add(declaration.UsingKeyword.IsKind(SyntaxKind.None) ? WithUsing(declaration) : declaration);
                    break;

                default:
                    statements.Add(statement);
                    break;
            }
        }

        return block.WithStatements(SyntaxFactory.List(statements));
    }

    // logger.EnterScope("x"); … logger.LeaveScope("x"); → using (logger.EnterMethod("x")) { … }
    private List<StatementSyntax> Scopes(SyntaxList<StatementSyntax> statements)
    {
        var result = statements.ToList();
        for (var i = 0; i < result.Count; i++)
        {
            if (Call(result[i]) is not { Method: "EnterScope" } enter)
            {
                if (Call(result[i]) is { Method: "LeaveScope" } stray)
                {
                    context.Todo($"'{stray.Logger}.LeaveScope()' had no EnterScope before it in the same block and was removed; check the scope");
                    result.RemoveAt(i--);
                }

                continue;
            }

            var end = result.FindIndex(i + 1, s => Call(s) is { Method: "LeaveScope" } leave && string.Equals(leave.Logger, enter.Logger, StringComparison.Ordinal));
            var scope = Rename(enter.Invocation, "EnterMethod").WithoutTrivia();

            if (end < 0 || DeclaresWhatIsUsedLater(result, i, end))
            {
                context.Todo($"'{enter.Logger}.EnterScope' became a using declaration, so the scope now ends with the block rather than at LeaveScope; check where it should end");
                result[i] = UsingDeclaration("scope", scope, result[i]);
                if (end >= 0)
                    result.RemoveAt(end);

                continue;
            }

            var inner = SyntaxFactory.Block(result.Skip(i + 1).Take(end - i - 1));
            var wrapped = SyntaxFactory.UsingStatement(null, scope, inner).WithLeadingTrivia(result[i].GetLeadingTrivia()).WithAdditionalAnnotations(Formatter.Annotation);
            result.RemoveRange(i, end - i + 1);
            result.Insert(i, wrapped);
        }

        return result;
    }

    // A local declared inside the bracket and used after it would go out of scope inside a using block.
    private static bool DeclaresWhatIsUsedLater(List<StatementSyntax> statements, int start, int end)
    {
        var declared = statements.Skip(start + 1).Take(end - start - 1)
            .OfType<LocalDeclarationStatementSyntax>()
            .SelectMany(d => d.Declaration.Variables.Select(v => v.Identifier.ValueText))
            .ToList();

        return declared.Count > 0 && statements.Skip(end + 1)
            .SelectMany(s => s.DescendantNodes().OfType<IdentifierNameSyntax>())
            .Any(n => declared.Contains(n.Identifier.ValueText, StringComparer.Ordinal));
    }

    // var logger = this.EnterMethod(); — named by a LeaveMethod the body had, or already a using declaration.
    private string? EnteredLocal(LocalDeclarationStatementSyntax declaration)
    {
        if (declaration.Declaration.Variables.Count != 1)
            return null;

        var variable = declaration.Declaration.Variables[0];
        var name = variable.Identifier.ValueText;
        var entersMethod = variable.Initializer?.Value is InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax { Name.Identifier.ValueText: "EnterMethod" } };

        return entersMethod && _left.Contains(name) ? name : null;
    }

    private (string Logger, string Method, InvocationExpressionSyntax Invocation)? Call(StatementSyntax statement) =>
        statement is ExpressionStatementSyntax { Expression: InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax access } invocation }
        && loggers.Contains(access.Expression.ToString())
            ? (access.Expression.ToString(), access.Name.Identifier.ValueText, invocation)
            : null;

    private static InvocationExpressionSyntax Rename(InvocationExpressionSyntax invocation, string method)
    {
        var access = (MemberAccessExpressionSyntax)invocation.Expression;
        return invocation.WithExpression(access.WithName(SyntaxFactory.IdentifierName(method)));
    }

    // In a block a removed statement just goes; as the body of an if or a loop it leaves an empty block.
    private static BlockSyntax? Removed(StatementSyntax statement) =>
        statement.Parent is BlockSyntax ? null : SyntaxFactory.Block().WithTriviaFrom(statement);

    private static LocalDeclarationStatementSyntax UsingDeclaration(string name, ExpressionSyntax value, SyntaxNode like) =>
        SyntaxFactory.ParseStatement("using var " + name + " = " + value + ";")
            .WithTriviaFrom(like) is LocalDeclarationStatementSyntax declaration
            ? declaration
            : throw new InvalidOperationException("A using declaration did not parse as one.");

    private static LocalDeclarationStatementSyntax WithoutUsing(LocalDeclarationStatementSyntax declaration)
    {
        var leading = declaration.UsingKeyword.LeadingTrivia;
        var stripped = declaration.WithUsingKeyword(default);
        return stripped.WithLeadingTrivia(leading);
    }

    private static LocalDeclarationStatementSyntax WithUsing(LocalDeclarationStatementSyntax declaration)
    {
        var leading = declaration.GetLeadingTrivia();
        return declaration.WithoutLeadingTrivia()
            .WithUsingKeyword(SyntaxFactory.Token(leading, SyntaxKind.UsingKeyword, SyntaxFactory.TriviaList(SyntaxFactory.Space)));
    }
}
