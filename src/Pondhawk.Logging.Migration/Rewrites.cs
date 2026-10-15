// Copyright (c) Pond Hawk Technologies Inc. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Formatting;
using Microsoft.CodeAnalysis.Operations;

namespace Pondhawk.Logging.Migration;

/// <summary>
/// The rewrite for each rule. Each is worked out from the original node and its meaning, and applied to the
/// node as it stands once any rewrites inside it are done — so nested Fabrica.Watch calls rewrite together.
/// New names are written fully qualified (<c>global::…</c>) and reduced afterwards, so a rewrite never
/// leaves a name that is ambiguous while <c>using Fabrica.Watch;</c> is still there. What a rewrite cannot
/// settle it still makes compile, and marks with a TODO.
/// </summary>
internal static class Rewrites
{
    public const string Logging = "global::Microsoft.Extensions.Logging.";
    public const string Pondhawk = "global::Pondhawk.Logging.";

    private delegate SyntaxNode Rewriter(SyntaxNode current, RewriteContext context);

    /// <summary>The node a diagnostic is about and how to rewrite it, or null when it no longer applies.</summary>
    public static (SyntaxNode Node, Func<SyntaxNode, SyntaxNode> Rewrite)? For(Diagnostic diagnostic, SyntaxNode root, SemanticModel model, string newLine, CancellationToken cancellationToken)
    {
        var span = diagnostic.Location.SourceSpan;
        var node = root.FindNode(span, getInnermostNodeForTie: true).AncestorsAndSelf().FirstOrDefault(n => n.Span == span);
        if (node is null)
            return null;

        if (diagnostic.Id is "PHLM006" or "PHLM007")
            return Tracing(node, model, newLine, cancellationToken);

        if (diagnostic.Id is "PHLM020")
            return Events.For(node, model, newLine, cancellationToken);

        var invocation = node is InvocationExpressionSyntax ? model.GetOperation(node, cancellationToken) as IInvocationOperation : null;
        Rewriter? rewrite = diagnostic.Id switch
        {
            "PHLM001" or "PHLM002" or "PHLM003" or "PHLM004" => Level(invocation, model),
            "PHLM005" => LevelEnabled(node, model, cancellationToken),
            "PHLM008" => Context(invocation, model),
            "PHLM009" => InspectName(invocation, model),
            "PHLM011" => Pretty(invocation),
            "PHLM012" => FactoryLocator(invocation),
            "PHLM013" => ObjectExtension(invocation),
            "PHLM014" => QualifiedType(node, "Pondhawk.Logging."),
            "PHLM016" => QualifiedType(node, "Microsoft.Extensions.Logging."),
            _ => null,
        };

        if (rewrite is null)
            return null;

        // A call standing as a statement may become another statement, and takes its TODOs above it.
        if (node is InvocationExpressionSyntax && node.Parent is ExpressionStatementSyntax statement)
        {
            return (statement, current =>
            {
                var now = (ExpressionStatementSyntax)current;
                var context = new RewriteContext(isStatement: true, newLine);
                var result = rewrite(now.Expression, context);
                var replaced = result is StatementSyntax s ? s.WithTriviaFrom(now) : now.WithExpression((ExpressionSyntax)result);
                return context.Mark(replaced);
            });
        }

        return (node, current =>
        {
            var context = new RewriteContext(isStatement: false, newLine);
            return context.MarkInline(rewrite(current, context));
        });
    }

    // logger.Debug(…) in any of its shapes → logger.LogDebug(template, …); a lazy one behind an IsEnabled check.
    private static Rewriter? Level(IInvocationOperation? invocation, SemanticModel model)
    {
        if (invocation is null || LevelCall.Match(invocation) is not { } call)
            return null;

        var original = (InvocationExpressionSyntax)invocation.Syntax;
        return (current, context) =>
        {
            var now = (InvocationExpressionSyntax)current;
            var messages = new Messages(model, original, now, context);
            var arguments = new List<string>();
            if (messages.Current(call.Exception) is { } exception)
                arguments.Add(exception);

            arguments.AddRange(messages.For(call));
            var renamed = Renamed(now, call.Method, arguments);
            return call.Shape == LevelShape.Lazy ? Guarded(renamed, call.Level, context) : renamed;
        };
    }

    private static SyntaxNode Guarded(InvocationExpressionSyntax call, string level, RewriteContext context)
    {
        if (!context.IsStatement)
        {
            context.Todo("the message used to be built only when the level was on; it is now built every time");
            return call;
        }

        var logger = ((MemberAccessExpressionSyntax)call.Expression).Expression;
        if (!IsSimple(logger))
            context.Todo($"'{logger}' is now evaluated twice, once for the level check");

        return SyntaxFactory.ParseStatement("if (" + logger + ".IsEnabled(" + Logging + "LogLevel." + level + ")) " + call.WithoutTrivia() + ";");
    }

    // logger.IsDebugEnabled → logger.IsEnabled(LogLevel.Debug)
    private static Rewriter? LevelEnabled(SyntaxNode node, SemanticModel model, CancellationToken cancellationToken)
    {
        if (node is not MemberAccessExpressionSyntax access
            || model.GetSymbolInfo(access, cancellationToken).Symbol is not IPropertySymbol property
            || LevelCall.LevelOfProperty(property.Name) is not { } level)
            return null;

        return (current, _) =>
        {
            var now = (MemberAccessExpressionSyntax)current;
            var target = now.WithName(SyntaxFactory.IdentifierName("IsEnabled")).WithoutTrivia();
            var arguments = SyntaxFactory.ParseArgumentList("(" + Logging + "LogLevel." + level + ")");
            return SyntaxFactory.InvocationExpression(target, arguments).WithTriviaFrom(now);
        };
    }

    // ErrorWithContext(exception, context) gets a string message; any other …WithContext becomes LogObject + LogX.
    private static Rewriter? Context(IInvocationOperation? invocation, SemanticModel model)
    {
        if (invocation is null || ContextCall.Match(invocation) is not { } call)
            return null;

        var original = (InvocationExpressionSyntax)invocation.Syntax;
        var exception = FabricaWatch.Written(call.Exception);
        var state = FabricaWatch.Written(call.Context);

        return (current, context) =>
        {
            var now = (InvocationExpressionSyntax)current;
            var messages = new Messages(model, original, now, context);
            var (exceptionText, stateText) = (messages.Current(exception), messages.Current(state)!);

            if (string.Equals(call.Level, "Error", StringComparison.Ordinal) && exceptionText is not null)
                return Renamed(now, "ErrorWithContext", [exceptionText, stateText, ErrorMessage(call.Message, exception, messages, context)]);

            var logger = ((MemberAccessExpressionSyntax)now.Expression).Expression.ToString();
            var kind = call.Message is null ? MessageKind.None : LevelCall.KindOf(call.Message);
            var arguments = (exceptionText is null ? [] : new[] { exceptionText }).Concat(messages.Simple(call.Message, kind, exception));
            var log = logger + "." + call.Method + "(" + string.Join(", ", arguments) + ")";
            var logContext = logger + ".LogObject(" + Logging + "LogLevel." + call.Level + ", \"Context\", " + stateText + ")";

            if (!context.IsStatement)
            {
                context.Todo($"the context object is no longer logged; log it with LogObject(LogLevel.{call.Level}, \"Context\", {stateText})");
                return SyntaxFactory.ParseExpression(log);
            }

            var block = "{" + context.NewLine + logContext + ";" + context.NewLine + log + ";" + context.NewLine + "}";
            return SyntaxFactory.ParseStatement(block).WithAdditionalAnnotations(Formatter.Annotation);
        };
    }

    private static string ErrorMessage(IArgumentOperation? message, ExpressionSyntax? exception, Messages messages, RewriteContext context)
    {
        if (FabricaWatch.Written(message) is { } written)
            return "$\"{" + messages.Current(written) + "}\"";

        if (exception is IdentifierNameSyntax)
            return messages.Current(exception) + ".Message";

        context.Todo("ErrorWithContext needs a message; say what failed");
        return "\"Error\"";
    }

    // logger.Inspect(42, value) → logger.Inspect("42", value)
    private static Rewriter? InspectName(IInvocationOperation? invocation, SemanticModel model)
    {
        if (invocation is null || FabricaWatch.Argument(invocation, 1) is not { } name || FabricaWatch.Written(FabricaWatch.Argument(invocation, 2)) is not { } value)
            return null;

        var original = (InvocationExpressionSyntax)invocation.Syntax;
        var constant = LevelCall.Unwrapped(name).ConstantValue;

        return (current, context) =>
        {
            var now = (InvocationExpressionSyntax)current;
            var messages = new Messages(model, original, now, context);
            var text = constant.HasValue
                ? SyntaxFactory.Literal(Convert.ToString(constant.Value, CultureInfo.InvariantCulture) ?? string.Empty).Text
                : "$\"{" + messages.Current(FabricaWatch.Written(name)) + "}\"";

            return Renamed(now, "Inspect", [text, messages.Current(value)!]);
        };
    }

    // logger.LogJson(title, json, pretty) → logger.LogJson(title, json)
    private static Rewriter? Pretty(IInvocationOperation? invocation)
    {
        if (invocation is null)
            return null;

        var method = FabricaWatch.Declared(invocation).Name;
        return (current, _) =>
        {
            var now = (InvocationExpressionSyntax)current;
            return Renamed(now, method, now.ArgumentList.Arguments.Take(2).Select(a => a.Expression.ToString()));
        };
    }

    // WatchFactoryLocator.Factory.GetLogger(category) → LoggingFactoryLocator.GetFactory().CreateLogger(category)
    private static Rewriter? FactoryLocator(IInvocationOperation? invocation)
    {
        if (invocation is null || !Acquisition.IsLocatorGetLogger(invocation))
            return null;

        var original = (InvocationExpressionSyntax)invocation.Syntax;
        var category = invocation.Arguments.FirstOrDefault(a => a.Parameter?.Ordinal == 0 && !FabricaWatch.Declared(invocation).IsGenericMethod);

        return (current, _) =>
        {
            var now = (InvocationExpressionSyntax)current;
            var name = ((MemberAccessExpressionSyntax)now.Expression).Name;
            var typeArguments = name is GenericNameSyntax generic ? generic.TypeArgumentList.ToString() : string.Empty;
            var text = Pondhawk + "LoggingFactoryLocator.GetFactory().CreateLogger" + typeArguments + "(" + Text(FabricaWatch.Written(category), original, now) + ")";
            return SyntaxFactory.ParseExpression(text).WithTriviaFrom(now);
        };
    }

    // this.EnterMethod<T>() → this.EnterMethod(); this.EnterMethodSlim(category) → …CreateLogger(category).EnterMethod()
    private static Rewriter? ObjectExtension(IInvocationOperation? invocation)
    {
        if (invocation is null || !Acquisition.NeedsObjectRewrite(invocation))
            return null;

        var original = (InvocationExpressionSyntax)invocation.Syntax;
        var slim = string.Equals(FabricaWatch.Declared(invocation).Name, "EnterMethodSlim", StringComparison.Ordinal);
        var method = Written(invocation, "name");
        var category = Written(invocation, "category");

        return (current, _) =>
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
    private static Rewriter? QualifiedType(SyntaxNode node, string target)
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
        return (current, _) => SyntaxFactory.ParseName(prefix + target + last).WithTriviaFrom(current);
    }

    // A function body's method and scope tracing, rewritten as one.
    private static (SyntaxNode, Func<SyntaxNode, SyntaxNode>)? Tracing(SyntaxNode node, SemanticModel model, string newLine, CancellationToken cancellationToken)
    {
        var function = node.Ancestors().FirstOrDefault(a => a is BaseMethodDeclarationSyntax or AccessorDeclarationSyntax or LocalFunctionStatementSyntax or AnonymousFunctionExpressionSyntax);
        var body = function switch
        {
            BaseMethodDeclarationSyntax m => m.Body,
            AccessorDeclarationSyntax a => a.Body,
            LocalFunctionStatementSyntax l => l.Body,
            AnonymousFunctionExpressionSyntax f => f.Block,
            _ => null,
        };

        if (body is null)
            return null;

        var loggers = new HashSet<string>(
            body.DescendantNodes().OfType<InvocationExpressionSyntax>()
                .Where(i => model.GetOperation(i, cancellationToken) is IInvocationOperation op && IsTracing(op))
                .Select(i => ((MemberAccessExpressionSyntax)i.Expression).Expression.ToString()),
            StringComparer.Ordinal);

        // using var logger = this.GetLogger(); — a using with nothing to dispose once the logger is migrated.
        var undisposed = new HashSet<string>(
            body.DescendantNodes()
                .Where(d => Disposal.IsNeedless(d, model, cancellationToken))
                .Select(d => Disposal.Disposed(d)!.Variables[0].Identifier.ValueText),
            StringComparer.Ordinal);

        // logger.Dispose(); on a plain logger — nothing to dispose once it is migrated.
        foreach (var statement in body.DescendantNodes().OfType<ExpressionStatementSyntax>().Where(s => Disposal.IsNeedlessDispose(s, model, cancellationToken)))
            undisposed.Add(((MemberAccessExpressionSyntax)((InvocationExpressionSyntax)statement.Expression).Expression).Expression + ".Dispose()");

        return (body, current => new Tracing(loggers, undisposed, new RewriteContext(isStatement: false, newLine)).Rewrite((BlockSyntax)current));
    }

    private static readonly string[] TracingMethods = ["EnterMethod", "LeaveMethod", "EnterScope", "LeaveScope"];

    private static bool IsTracing(IInvocationOperation invocation) =>
        TracingMethods.Any(name => FabricaWatch.Calls(invocation, FabricaWatch.LoggerExtensions, name));

    private static bool IsSimple(ExpressionSyntax expression) =>
        expression is IdentifierNameSyntax or ThisExpressionSyntax
        || (expression is MemberAccessExpressionSyntax access && IsSimple(access.Expression));

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
