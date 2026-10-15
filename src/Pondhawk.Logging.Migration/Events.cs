// Copyright (c) Pond Hawk Technologies Inc. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;

namespace Pondhawk.Logging.Migration;

/// <summary>
/// An event built by hand and logged at once (PHLM020) — <c>var ev = logger.CreateEvent(…); logger.LogEvent(ev);</c>,
/// <c>ev</c> used for nothing else — becomes the one call that says the same: a typed payload's
/// <c>LogJson/LogXml/LogSql/LogYaml/LogText(level, title, payload)</c>, an object's <c>LogObject(level, title, value)</c>,
/// an exception with context's <c>ErrorWithContext</c> (or <c>LogObject</c> and <c>LogX(exception, …)</c> below Error),
/// or a title's <c>LogX(title)</c>. An event changed, passed on, or with a level or payload type that is not a
/// constant stays reported only.
/// </summary>
internal static class Events
{
    private static readonly Dictionary<string, (string Level, string Method)> Levels = new(StringComparer.Ordinal)
    {
        ["Trace"] = ("Trace", "LogTrace"),
        ["Debug"] = ("Debug", "LogDebug"),
        ["Info"] = ("Information", "LogInformation"),
        ["Warning"] = ("Warning", "LogWarning"),
        ["Error"] = ("Error", "LogError"),
    };

    private static readonly Dictionary<string, string> Payloads = new(StringComparer.Ordinal)
    {
        ["None"] = "LogText",
        ["Text"] = "LogText",
        ["Json"] = "LogJson",
        ["Xml"] = "LogXml",
        ["Sql"] = "LogSql",
        ["Yaml"] = "LogYaml",
    };

    /// <summary>
    /// The block an event pair is in and how to rewrite it, or null when the event the diagnostic is about cannot be
    /// rewritten. Every pair in the block that can be is rewritten together.
    /// </summary>
    public static (SyntaxNode Block, Func<SyntaxNode, SyntaxNode> Rewrite)? For(SyntaxNode node, SemanticModel model, string newLine, CancellationToken cancellationToken)
    {
        if (node.FirstAncestorOrSelf<StatementSyntax>()?.Parent is not BlockSyntax block)
            return null;

        var plans = new Dictionary<string, Plan>(StringComparer.Ordinal);
        foreach (var (declaration, _, name) in Pairs(block))
        {
            var creation = (InvocationExpressionSyntax)declaration.Declaration.Variables[0].Initializer!.Value;
            if (model.GetOperation(creation, cancellationToken) is IInvocationOperation invocation && Plan.Of(invocation) is { } plan)
                plans[name] = plan;
        }

        var statement = node.FirstAncestorOrSelf<StatementSyntax>()!;
        var covered = Pairs(block).Any(p => plans.ContainsKey(p.Name) && (p.Declaration == statement || p.Logged == statement));
        if (!covered)
            return null;

        return (block, current => Rewrite((BlockSyntax)current, plans, newLine));
    }

    private static BlockSyntax Rewrite(BlockSyntax block, Dictionary<string, Plan> plans, string newLine)
    {
        var statements = block.Statements.ToList();
        foreach (var (declaration, logged, name) in Pairs(block).Where(p => plans.ContainsKey(p.Name)).Reverse())
        {
            var creation = (InvocationExpressionSyntax)declaration.Declaration.Variables[0].Initializer!.Value;
            var logger = ((MemberAccessExpressionSyntax)creation.Expression).Expression.ToString();
            var arguments = creation.ArgumentList.Arguments.Select(a => a.Expression.ToString()).ToList();

            var replacements = plans[name].Statements(logger, arguments)
                .Select((text, i) => SyntaxFactory.ParseStatement(text).WithTriviaFrom(i == 0 ? declaration : logged).WithTrailingTrivia(SyntaxFactory.EndOfLine(newLine)))
                .ToList();

            var at = statements.IndexOf(declaration);
            statements.Remove(logged);
            statements.RemoveAt(at);
            statements.InsertRange(at, replacements.Select((s, i) => i == 0 ? s.WithLeadingTrivia(declaration.GetLeadingTrivia()) : s.WithLeadingTrivia(declaration.GetLeadingTrivia().Where(t => t.IsKind(SyntaxKind.WhitespaceTrivia)))));
        }

        return block.WithStatements(SyntaxFactory.List(statements));
    }

    // var ev = logger.CreateEvent(…); … logger.LogEvent(ev); — on the same logger, ev used nowhere else in the block.
    private static IEnumerable<(LocalDeclarationStatementSyntax Declaration, ExpressionStatementSyntax Logged, string Name)> Pairs(BlockSyntax block)
    {
        var statements = block.Statements;
        for (var i = 0; i < statements.Count; i++)
        {
            if (statements[i] is not LocalDeclarationStatementSyntax { Declaration.Variables: [{ Initializer.Value: InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax { Name.Identifier.ValueText: "CreateEvent" } create } } variable] } declaration)
                continue;

            var name = variable.Identifier.ValueText;
            var logger = create.Expression.ToString();
            var logged = statements.Skip(i + 1).OfType<ExpressionStatementSyntax>().FirstOrDefault(s =>
                s.Expression is InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax { Name.Identifier.ValueText: "LogEvent" } log, ArgumentList.Arguments: [{ Expression: IdentifierNameSyntax arg }] }
                && string.Equals(arg.Identifier.ValueText, name, StringComparison.Ordinal)
                && string.Equals(log.Expression.ToString(), logger, StringComparison.Ordinal));

            if (logged is null)
                continue;

            var uses = statements.Skip(i + 1).SelectMany(s => s.DescendantNodes().OfType<IdentifierNameSyntax>()).Count(n => string.Equals(n.Identifier.ValueText, name, StringComparison.Ordinal));
            if (uses == 1)
                yield return (declaration, logged, name);
        }
    }

    /// <summary>What an event becomes, worked out from the original <c>CreateEvent</c> call.</summary>
    private sealed class Plan(Func<string, List<string>, List<string>> statements)
    {
        public List<string> Statements(string logger, List<string> arguments) => statements(logger, arguments);

        public static Plan? Of(IInvocationOperation invocation)
        {
            var parameters = invocation.TargetMethod.Parameters;
            if (parameters.Length < 2 || Constant(invocation, 0) is not { } levelName || !Levels.TryGetValue(levelName, out var level))
                return null;

            var title = invocation.Arguments.FirstOrDefault(a => a.Parameter?.Ordinal == 1);
            var titleKind = LevelCall.KindOf(title);
            var titleIsString = title is not null && LevelCall.Unwrapped(title).Type?.SpecialType == SpecialType.System_String;
            string Title(List<string> a) => titleIsString ? a[1] : "$\"{" + a[1] + "}\"";

            var logLevel = Rewrites.Logging + "LogLevel." + level.Level;
            switch (parameters.Length)
            {
                case 2:
                    return new Plan((logger, a) => [logger + "." + level.Method + "(" + Message(titleKind, a[1]) + ");"]);

                case 3:
                    return new Plan((logger, a) => [logger + ".LogObject(" + logLevel + ", " + Title(a) + ", " + a[2] + ");"]);

                case 4 when parameters[2].Type.TypeKind == TypeKind.Enum:
                    if (Constant(invocation, 2) is not { } payload || !Payloads.TryGetValue(payload, out var method))
                        return null;

                    return new Plan((logger, a) => [logger + "." + method + "(" + logLevel + ", " + Title(a) + ", " + a[3] + ");"]);

                case 4:
                    return new Plan((logger, a) => string.Equals(level.Level, "Error", StringComparison.Ordinal)
                        ? [logger + ".ErrorWithContext(" + a[2] + ", " + a[3] + ", " + Title(a) + ");"]
                        : [logger + ".LogObject(" + logLevel + ", \"Context\", " + a[3] + ");", logger + "." + level.Method + "(" + a[2] + ", " + Message(titleKind, a[1]) + ");"]);

                default:
                    return null;
            }
        }

        // An enum argument written as a constant: Level.Info → "Info".
        private static string? Constant(IInvocationOperation invocation, int ordinal)
        {
            var argument = invocation.Arguments.FirstOrDefault(a => a.Parameter?.Ordinal == ordinal);
            return argument is null ? null : LevelCall.Unwrapped(argument) is IFieldReferenceOperation { Field.HasConstantValue: true } field ? field.Field.Name : null;
        }

        private static string Message(MessageKind kind, string text) =>
            kind switch
            {
                MessageKind.Constant => text,
                MessageKind.Text => "\"{Message}\", " + text,
                MessageKind.Value => "\"{Value}\", " + text,
                _ => "\"\"",
            };
    }
}
