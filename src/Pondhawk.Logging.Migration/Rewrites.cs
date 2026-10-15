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
/// The rewrite for each rule. Each is worked out from the original node and its meaning, and applied to the
/// node as it stands once any rewrites inside it are done — so nested Fabrica.Watch calls rewrite together.
/// New names are written fully qualified (<c>global::…</c>) and reduced afterwards, so a rewrite never
/// leaves a name that is ambiguous while <c>using Fabrica.Watch;</c> is still there.
/// </summary>
internal static class Rewrites
{
    public const string Logging = "global::Microsoft.Extensions.Logging.";
    public const string Pondhawk = "global::Pondhawk.Logging.";

    /// <summary>The node a diagnostic is about and how to rewrite it, or null when it no longer applies.</summary>
    public static (SyntaxNode Node, Func<SyntaxNode, SyntaxNode> Rewrite)? For(Diagnostic diagnostic, SyntaxNode root, SemanticModel model, CancellationToken cancellationToken)
    {
        var span = diagnostic.Location.SourceSpan;
        var node = root.FindNode(span, getInnermostNodeForTie: true).AncestorsAndSelf().FirstOrDefault(n => n.Span == span);
        if (node is null)
            return null;

        Func<SyntaxNode, SyntaxNode>? rewrite = diagnostic.Id switch
        {
            "PHLM001" => LevelCall(node, model, cancellationToken),
            "PHLM005" => LevelEnabled(node, model, cancellationToken),
            "PHLM012" => FactoryLocator(node, model, cancellationToken),
            "PHLM013" => ObjectExtension(node, model, cancellationToken),
            "PHLM014" => QualifiedType(node, "Pondhawk.Logging."),
            "PHLM016" => QualifiedType(node, "Microsoft.Extensions.Logging."),
            _ => null,
        };

        return rewrite is null ? null : (node, rewrite);
    }

    private static IInvocationOperation? Invocation(SyntaxNode node, SemanticModel model, CancellationToken cancellationToken) =>
        node is InvocationExpressionSyntax ? model.GetOperation(node, cancellationToken) as IInvocationOperation : null;

    // logger.Debug(message) → logger.LogDebug(template, …)
    private static Func<SyntaxNode, SyntaxNode>? LevelCall(SyntaxNode node, SemanticModel model, CancellationToken cancellationToken)
    {
        if (Invocation(node, model, cancellationToken) is not { } invocation || Migration.LevelCall.Match(invocation) is not { } call)
            return null;

        var original = (InvocationExpressionSyntax)node;
        return current =>
        {
            var now = (InvocationExpressionSyntax)current;
            var arguments = new List<string>();
            var exception = Text(call.Exception, original, now);
            if (exception is not null)
                arguments.Add(exception);

            var message = Text(call.Message, original, now);
            arguments.AddRange(call.Kind switch
            {
                MessageKind.Constant => [message!],
                MessageKind.Text => ["\"{Message}\"", message!],
                MessageKind.Value => ["\"{Value}\"", message!],
                _ when call.Exception is IdentifierNameSyntax => ["\"{Message}\"", exception + ".Message"],
                _ => ["\"\""],
            });

            return Renamed(now, call.Method, arguments);
        };
    }

    // logger.IsDebugEnabled → logger.IsEnabled(LogLevel.Debug)
    private static Func<SyntaxNode, SyntaxNode>? LevelEnabled(SyntaxNode node, SemanticModel model, CancellationToken cancellationToken)
    {
        if (node is not MemberAccessExpressionSyntax access
            || model.GetSymbolInfo(access, cancellationToken).Symbol is not IPropertySymbol property
            || Migration.LevelCall.LevelOfProperty(property.Name) is not { } level)
            return null;

        return current =>
        {
            var now = (MemberAccessExpressionSyntax)current;
            var target = now.WithName(SyntaxFactory.IdentifierName("IsEnabled")).WithoutTrivia();
            var arguments = SyntaxFactory.ParseArgumentList("(" + Logging + "LogLevel." + level + ")");
            return SyntaxFactory.InvocationExpression(target, arguments).WithTriviaFrom(now);
        };
    }

    // WatchFactoryLocator.Factory.GetLogger(category) → LoggingFactoryLocator.GetFactory().CreateLogger(category)
    private static Func<SyntaxNode, SyntaxNode>? FactoryLocator(SyntaxNode node, SemanticModel model, CancellationToken cancellationToken)
    {
        if (Invocation(node, model, cancellationToken) is not { } invocation || !Acquisition.IsLocatorGetLogger(invocation))
            return null;

        var original = (InvocationExpressionSyntax)node;
        var category = invocation.Arguments.FirstOrDefault(a => a.Parameter?.Ordinal == 0 && !FabricaWatch.Declared(invocation).IsGenericMethod);

        return current =>
        {
            var now = (InvocationExpressionSyntax)current;
            var name = ((MemberAccessExpressionSyntax)now.Expression).Name;
            var typeArguments = name is GenericNameSyntax generic ? generic.TypeArgumentList.ToString() : string.Empty;
            var text = Pondhawk + "LoggingFactoryLocator.GetFactory().CreateLogger" + typeArguments + "(" + Text(FabricaWatch.Written(category), original, now) + ")";
            return SyntaxFactory.ParseExpression(text).WithTriviaFrom(now);
        };
    }

    // this.EnterMethod<T>() → this.EnterMethod(); this.EnterMethodSlim(category) → …CreateLogger(category).EnterMethod()
    private static Func<SyntaxNode, SyntaxNode>? ObjectExtension(SyntaxNode node, SemanticModel model, CancellationToken cancellationToken)
    {
        if (Invocation(node, model, cancellationToken) is not { } invocation || !Acquisition.NeedsObjectRewrite(invocation))
            return null;

        var original = (InvocationExpressionSyntax)node;
        var slim = string.Equals(FabricaWatch.Declared(invocation).Name, "EnterMethodSlim", StringComparison.Ordinal);
        var method = Written(invocation, "name");
        var category = Written(invocation, "category");

        return current =>
        {
            var now = (InvocationExpressionSyntax)current;
            var name = Text(method, original, now);
            var arguments = name is null ? [] : new List<string> { name };

            if (!slim)
                return Renamed(now, "EnterMethod", arguments);

            var text = Pondhawk + "LoggingFactoryLocator.GetFactory().CreateLogger(" + Text(category, original, now) + ").EnterMethod(" + name + ")";
            return SyntaxFactory.ParseExpression(text).WithTriviaFrom(now);
        };
    }

    // Fabrica.Watch.ILogger → Microsoft.Extensions.Logging.ILogger; Fabrica.Watch.SensitiveAttribute → Pondhawk.Logging's
    private static Func<SyntaxNode, SyntaxNode>? QualifiedType(SyntaxNode node, string target)
    {
        var last = node switch
        {
            QualifiedNameSyntax q => q.Right.Identifier.Text,
            MemberAccessExpressionSyntax m => m.Name.Identifier.Text,
            _ => null,
        };

        if (last is null)
            return null;

        // A using alias needs a name it can resolve on its own; elsewhere the name is reduced afterwards.
        var prefix = node.FirstAncestorOrSelf<UsingDirectiveSyntax>() is null ? "global::" : string.Empty;
        return current => SyntaxFactory.ParseName(prefix + target + last).WithTriviaFrom(current);
    }

    private static ExpressionSyntax? Written(IInvocationOperation invocation, string parameter) =>
        FabricaWatch.Written(invocation.Arguments.FirstOrDefault(a => string.Equals(a.Parameter?.Name, parameter, StringComparison.Ordinal)));

    // The current text of an argument written in the original call, found by its position.
    private static string? Text(ExpressionSyntax? written, InvocationExpressionSyntax original, InvocationExpressionSyntax current)
    {
        if (written?.Parent is not ArgumentSyntax argument)
            return null;

        var index = original.ArgumentList.Arguments.IndexOf(argument);
        return index < 0 || index >= current.ArgumentList.Arguments.Count ? null : current.ArgumentList.Arguments[index].Expression.ToString();
    }

    // receiver.Name(arguments), keeping the receiver and the call's surrounding text as they were.
    private static InvocationExpressionSyntax Renamed(InvocationExpressionSyntax invocation, string method, IEnumerable<string> arguments)
    {
        var access = (MemberAccessExpressionSyntax)invocation.Expression;
        var renamed = access.WithName(SyntaxFactory.IdentifierName(method).WithTriviaFrom(access.Name));
        var list = SyntaxFactory.ParseArgumentList("(" + string.Join(", ", arguments) + ")").WithTriviaFrom(invocation.ArgumentList);
        return invocation.WithExpression(renamed).WithArgumentList(list);
    }
}
