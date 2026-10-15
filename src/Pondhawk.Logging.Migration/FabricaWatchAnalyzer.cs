// Copyright (c) Pond Hawk Technologies Inc. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System;
using System.Collections.Immutable;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Diagnostics;
using Microsoft.CodeAnalysis.Operations;

namespace Pondhawk.Logging.Migration;

/// <summary>
/// Finds Fabrica.Watch logging code: what has a Microsoft.Extensions.Logging / Pondhawk.Logging equivalent, for
/// <see cref="FabricaWatchCodeFixProvider"/> to rewrite, and what does not, to report.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class FabricaWatchAnalyzer : DiagnosticAnalyzer
{
    /// <inheritdoc />
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = ImmutableArray.Create(
        Rules.LevelCall,
        Rules.Template,
        Rules.Parts,
        Rules.Lazy,
        Rules.LevelEnabled,
        Rules.MethodTrace,
        Rules.Scope,
        Rules.ContextCall,
        Rules.InspectName,
        Rules.ObjectLevel,
        Rules.Pretty,
        Rules.FactoryLocator,
        Rules.ObjectExtension,
        Rules.SharedType,
        Rules.Usings,
        Rules.LoggerType,
        Rules.Events,
        Rules.Bootstrap,
        Rules.Bridge,
        Rules.ScopeState,
        Rules.Unmigrated);

    /// <inheritdoc />
    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();

        context.RegisterOperationAction(AnalyzeInvocation, OperationKind.Invocation);
        context.RegisterOperationAction(AnalyzeCreation, OperationKind.ObjectCreation);
        context.RegisterOperationAction(AnalyzePropertyReference, OperationKind.PropertyReference);
        context.RegisterSyntaxNodeAction(AnalyzeQualifiedName, SyntaxKind.QualifiedName, SyntaxKind.SimpleMemberAccessExpression);
        context.RegisterSyntaxNodeAction(AnalyzeDisposal, SyntaxKind.LocalDeclarationStatement, SyntaxKind.UsingStatement, SyntaxKind.ExpressionStatement);
        context.RegisterSemanticModelAction(AnalyzeFile);
    }

    private static void AnalyzeInvocation(OperationAnalysisContext context)
    {
        var invocation = (IInvocationOperation)context.Operation;
        if (!FabricaWatch.Owns(invocation.TargetMethod) || FabricaWatch.Owns(context.ContainingSymbol))
            return;

        var (rule, arguments) = Classify(invocation);
        if (rule is not null)
            context.ReportDiagnostic(Diagnostic.Create(rule, invocation.Syntax.GetLocation(), arguments));
    }

    private static (DiagnosticDescriptor? Rule, object[] Arguments) Classify(IInvocationOperation invocation)
    {
        if (LevelCall.Match(invocation) is { } call)
        {
            var rule = call.Shape switch
            {
                LevelShape.Template => Rules.Template,
                LevelShape.Parts => Rules.Parts,
                LevelShape.Lazy => Rules.Lazy,
                _ => Rules.LevelCall,
            };

            return (rule, [call.Name, call.Method]);
        }

        // WatchFactoryLocator.Factory.GetLogger and EnterMethodSlim change the logger's type: AnalyzeFile reports them
        // once nothing else in the file needs a rule.
        var name = FabricaWatch.Declared(invocation).Name;
        if (Acquisition.IsLocatorGetLogger(invocation) || Acquisition.IsSlim(invocation))
            return (null, []);
        if (Acquisition.NeedsObjectRewrite(invocation))
            return (Rules.ObjectExtension, [name]);

        return (LoggerExtension(invocation, name) ?? ReportOnly(invocation, name), [name]);
    }

    // The rest of Fabrica.Watch's logger extensions, called as extensions.
    private static DiagnosticDescriptor? LoggerExtension(IInvocationOperation invocation, string name)
    {
        if (!FabricaWatch.Calls(invocation, FabricaWatch.LoggerExtensions, name))
            return null;

        if (ContextCall.Match(invocation) is { } withContext)
            return withContext.CarriesOver ? null : Rules.ContextCall;

        return name switch
        {
            "EnterMethod" when invocation.Syntax.Parent is ExpressionStatementSyntax && IsLeftInSameBody(invocation) => Rules.MethodTrace,
            "LeaveMethod" => Rules.MethodTrace,
            "EnterScope" or "LeaveScope" => Rules.Scope,
            "Inspect" when !CarriedOver.HasStringName(invocation) => Rules.InspectName,
            "LogObject" => Rules.ObjectLevel,
            "LogJson" or "LogXml" when FabricaWatch.Argument(invocation, 3) is { ArgumentKind: ArgumentKind.Explicit } => Rules.Pretty,
            _ => null,
        };
    }

    // logger.EnterMethod(); paired with a logger.LeaveMethod() in the same function. One without (enter, then
    // return the logger for the caller to dispose) is not a trace to close here, and carries over as it is.
    private static bool IsLeftInSameBody(IInvocationOperation invocation)
    {
        var logger = ((MemberAccessExpressionSyntax)((InvocationExpressionSyntax)invocation.Syntax).Expression).Expression.ToString();
        var body = invocation.Syntax.Ancestors().FirstOrDefault(a => a is BaseMethodDeclarationSyntax or AccessorDeclarationSyntax or LocalFunctionStatementSyntax or AnonymousFunctionExpressionSyntax);

        return body is not null && body.DescendantNodes().OfType<InvocationExpressionSyntax>().Any(i =>
            i.Expression is MemberAccessExpressionSyntax { Name.Identifier.ValueText: "LeaveMethod" } access
            && string.Equals(access.Expression.ToString(), logger, StringComparison.Ordinal));
    }

    // What has no equivalent: events built by hand, set-up, the MEL bridge, a logger's own state.
    private static DiagnosticDescriptor? ReportOnly(IInvocationOperation invocation, string name)
    {
        var declared = FabricaWatch.Declared(invocation);
        var container = FabricaWatch.NameOf(declared.ContainingType);

        if (string.Equals(container, FabricaWatch.Logger, StringComparison.Ordinal))
            return name is "CreateEvent" or "LogEvent" ? Rules.Events : name is "GetCurrentScope" or "SetCurrentScope" ? Rules.ScopeState : null;

        if (IsBridge(declared.ContainingType))
            return Rules.Bridge;

        var isBuilder = IsBuilder(declared.ContainingType)
            || (declared.IsExtensionMethod && declared.Parameters.FirstOrDefault()?.Type is { } target && IsBuilder(target))
            || (string.Equals(container, FabricaWatch.WatchFactoryLocator, StringComparison.Ordinal) && string.Equals(name, "SetFactory", StringComparison.Ordinal));

        return isBuilder ? Rules.Bootstrap : null;
    }

    private static void AnalyzeCreation(OperationAnalysisContext context)
    {
        var creation = (IObjectCreationOperation)context.Operation;
        if (creation.Type is not { } type || !FabricaWatch.Owns(type) || FabricaWatch.Owns(context.ContainingSymbol))
            return;

        var rule = IsBuilder(type) ? Rules.Bootstrap : IsBridge(type) ? Rules.Bridge : null;
        if (rule is not null)
            context.ReportDiagnostic(Diagnostic.Create(rule, creation.Syntax.GetLocation(), type.Name));
    }

    private static bool IsBuilder(ITypeSymbol type) =>
        string.Equals(type.Name, "WatchFactoryBuilder", StringComparison.Ordinal) && FabricaWatch.Owns(type);

    private static bool IsBridge(ITypeSymbol? type) =>
        type?.ContainingNamespace?.ToDisplayString() is { } ns
        && (string.Equals(ns, FabricaWatch.Namespace + ".Bridges", StringComparison.Ordinal) || ns.StartsWith(FabricaWatch.Namespace + ".Bridges.", StringComparison.Ordinal));

    private static void AnalyzePropertyReference(OperationAnalysisContext context)
    {
        var reference = (IPropertyReferenceOperation)context.Operation;
        if (FabricaWatch.Owns(context.ContainingSymbol)
            || reference.Syntax is not MemberAccessExpressionSyntax
            || !string.Equals(FabricaWatch.NameOf(reference.Property.ContainingType), FabricaWatch.Logger, StringComparison.Ordinal))
            return;

        if (LevelCall.LevelOfProperty(reference.Property.Name) is { } level)
            context.ReportDiagnostic(Diagnostic.Create(Rules.LevelEnabled, reference.Syntax.GetLocation(), reference.Property.Name, level));
        else if (string.Equals(reference.Property.Name, "Category", StringComparison.Ordinal))
            context.ReportDiagnostic(Diagnostic.Create(Rules.ScopeState, reference.Syntax.GetLocation(), "Category"));
    }

    // Fabrica.Watch.SensitiveAttribute and Fabrica.Watch.Sink.PayloadType written out in full. A simple name needs no
    // rewrite: the using swap carries it over. Fabrica.Watch.ILogger written out is AnalyzeFile's: it changes a type.
    private static void AnalyzeQualifiedName(SyntaxNodeAnalysisContext context)
    {
        if (FabricaWatch.Owns(context.ContainingSymbol))
            return;   // Fabrica.Watch's own source, vendored into the solution

        if (QualifiedRule(context.Node, context.SemanticModel, context.CancellationToken) is { } rule && !ReferenceEquals(rule, Rules.LoggerType))
            context.ReportDiagnostic(Diagnostic.Create(rule, context.Node.GetLocation(), context.Node.ToString()));
    }

    private static DiagnosticDescriptor? QualifiedRule(SyntaxNode node, SemanticModel model, System.Threading.CancellationToken cancellationToken)
    {
        if (node.Parent is QualifiedNameSyntax { Left: var left } && left == node)
            return null;   // part of a longer name; that one is looked at instead

        if (!FabricaWatch.IsQualifiedByNamespace(node, model))
            return null;

        var symbol = model.GetSymbolInfo(node, cancellationToken).Symbol;
        var type = symbol as INamedTypeSymbol ?? (symbol as IMethodSymbol is { MethodKind: MethodKind.Constructor } constructor ? constructor.ContainingType : null);

        return FabricaWatch.NameOf(type) switch
        {
            FabricaWatch.Logger => Rules.LoggerType,
            FabricaWatch.Sensitive or FabricaWatch.PayloadType => Rules.SharedType,
            _ => null,
        };
    }

    // using var logger = this.GetLogger(); — every Fabrica.Watch logger is disposable, a Microsoft.Extensions.Logging
    // one is not. Only an EnterMethod has a trace to close, so any other using goes.
    private static void AnalyzeDisposal(SyntaxNodeAnalysisContext context)
    {
        if (FabricaWatch.Owns(context.ContainingSymbol))
            return;

        if (context.Node is ExpressionStatementSyntax statement)
        {
            if (Disposal.IsNeedlessDispose(statement, context.SemanticModel, context.CancellationToken))
                context.ReportDiagnostic(Diagnostic.Create(Rules.MethodTrace, statement.GetLocation(), statement.Expression.ToString()));

            return;
        }

        if (!Disposal.IsNeedless(context.Node, context.SemanticModel, context.CancellationToken))
            return;

        var name = Disposal.Disposed(context.Node)!.Variables[0].Identifier.ValueText;
        context.ReportDiagnostic(Diagnostic.Create(Rules.MethodTrace, context.Node.GetLocation(), "using " + name));
    }

    // A file whose members give Fabrica.Watch's ILogger to other files — a base class's protected ILogger EnterMethod(),
    // a helper returning a logger — is where an application hands out its loggers, the part of a migration done
    // by hand. Swapping its usings would change those members' types under the files that call them, before their
    // calls are rewritten, and hide those calls from every rule. It keeps its usings until it is changed by hand.
    private static bool HandsOutFabricaTypes(SyntaxNode root, SemanticModel model, System.Threading.CancellationToken cancellationToken) =>
        root.DescendantNodes().OfType<MemberDeclarationSyntax>()
            .Where(m => m is not BaseTypeDeclarationSyntax and not BaseNamespaceDeclarationSyntax)
            .SelectMany(m => m is BaseFieldDeclarationSyntax field
                ? field.Declaration.Variables.Select(v => model.GetDeclaredSymbol(v, cancellationToken))
                : [model.GetDeclaredSymbol(m, cancellationToken)])
            .Any(s => s is not null && s.DeclaredAccessibility != Accessibility.Private && SignatureTypes(s).Any(MentionsFabrica));

    private static System.Collections.Generic.IEnumerable<ITypeSymbol> SignatureTypes(ISymbol symbol) =>
        symbol switch
        {
            IMethodSymbol method => method.Parameters.Select(p => p.Type).Prepend(method.ReturnType),
            IPropertySymbol property => property.Parameters.Select(p => p.Type).Prepend(property.Type),
            IFieldSymbol field => [field.Type],
            IEventSymbol @event => [@event.Type],
            _ => [],
        };

    // Only the logger hides calls: the rules find a call by the logger it is made on.
    private static bool MentionsFabrica(ITypeSymbol type) =>
        string.Equals(FabricaWatch.NameOf(type), FabricaWatch.Logger, StringComparison.Ordinal)
        || (type is INamedTypeSymbol { IsGenericType: true } generic && generic.TypeArguments.Any(MentionsFabrica))
        || (type is IArrayTypeSymbol array && MentionsFabrica(array.ElementType));

    // The rewrites that change a logger's type — WatchFactoryLocator.Factory.GetLogger, EnterMethodSlim, ILogger
    // written in full — wait until nothing else in the file needs a rule, since a call on a logger whose type has
    // changed no longer binds to Fabrica.Watch and no rule would find it. Once they are done, the usings can go.
    private static void AnalyzeFile(SemanticModelAnalysisContext context)
    {
        var model = context.SemanticModel;
        var cancellationToken = context.CancellationToken;
        var root = model.SyntaxTree.GetRoot(cancellationToken);
        if (root.DescendantNodes().OfType<BaseNamespaceDeclarationSyntax>().Any(n => FabricaWatch.IsFabricaNamespace(n.Name.ToString())))
            return;

        var sites = TypeChangingSites(root, model, cancellationToken);
        var blocked = false;
        var unmigrated = new System.Collections.Generic.List<(SyntaxNode Node, string What)>();

        foreach (var name in root.DescendantNodes().OfType<SimpleNameSyntax>())
        {
            // var binds to the type it stands for, but nothing is written there to migrate.
            if (name.IsVar || name.FirstAncestorOrSelf<UsingDirectiveSyntax>() is not null || sites.Any(s => s.Node.Span.Contains(name.Span)))
                continue;

            // Only what binds counts. A call that fails to bind against Fabrica.Watch is one a rewrite wrote for
            // the new API (logger.LogObject(LogLevel.Warning, …) on a logger still typed as Fabrica.Watch's).
            var symbol = model.GetSymbolInfo(name, cancellationToken).Symbol;
            if (!FabricaWatch.Owns(symbol) || CarriedOver.IsCarried(name, symbol!, model, cancellationToken))
                continue;

            blocked = true;
            if (Uncovered(name, symbol!, model, cancellationToken) is { } node)
                unmigrated.Add((node, Describe(symbol!)));
        }

        // The outermost of what is uncovered: WatchCorrelation.Begin(…), not also WatchCorrelation inside it.
        foreach (var (node, what) in unmigrated.Where(u => !unmigrated.Any(o => o.Node != u.Node && o.Node.Span.Contains(u.Node.Span))).GroupBy(u => u.Node.Span).Select(g => g.First()))
            context.ReportDiagnostic(Diagnostic.Create(Rules.Unmigrated, node.GetLocation(), what));

        if (blocked)
            return;

        foreach (var (node, rule, argument) in sites)
            context.ReportDiagnostic(Diagnostic.Create(rule, node.GetLocation(), argument));

        var usings = root.DescendantNodes().OfType<UsingDirectiveSyntax>().Where(FabricaWatch.IsFabricaUsing).ToList();
        if (sites.Count > 0 || usings.Count == 0 || HandsOutFabricaTypes(root, model, cancellationToken))
            return;

        foreach (var directive in usings)
            context.ReportDiagnostic(Diagnostic.Create(Rules.Usings, directive.GetLocation(), directive.Name!.ToString()));
    }

    // A Fabrica.Watch use no other rule reports — an API a copy of Fabrica.Watch added, a sink type — is reported
    // here, so what holds a file's usings is visible. Returns the node to report, or null when a rule covers it.
    private static SyntaxNode? Uncovered(SimpleNameSyntax name, ISymbol symbol, SemanticModel model, System.Threading.CancellationToken cancellationToken)
    {
        // Inside a call another rule reports — Level.Error in logger.CreateEvent(Level.Error, …) — it is covered there.
        foreach (var enclosing in name.Ancestors().OfType<InvocationExpressionSyntax>().Where(c => !c.Expression.Span.Contains(name.Span) || c.Expression is MemberAccessExpressionSyntax m && m.Expression.Span.Contains(name.Span)))
        {
            if (model.GetOperation(enclosing, cancellationToken) is IInvocationOperation reported && FabricaWatch.Owns(reported.TargetMethod) && Classify(reported).Rule is not null)
                return null;
        }

        var whole = FabricaWatch.Whole(name);
        while (whole.Parent is QualifiedNameSyntax or MemberAccessExpressionSyntax && FabricaWatch.IsQualifiedByNamespace(whole.Parent, model))
            whole = whole.Parent;

        if (symbol is INamespaceSymbol || QualifiedRule(whole, model, cancellationToken) is not null)
            return symbol is INamespaceSymbol && whole == FabricaWatch.Whole(name) ? null : QualifiedRule(whole, model, cancellationToken) is null ? whole : null;

        if (whole.Parent is InvocationExpressionSyntax call && model.GetOperation(call, cancellationToken) is IInvocationOperation invocation)
            return Classify(invocation).Rule is null && !Acquisition.IsLocatorGetLogger(invocation) && !Acquisition.IsSlim(invocation) ? call : null;

        if (symbol is IPropertySymbol property && string.Equals(FabricaWatch.NameOf(property.ContainingType), FabricaWatch.Logger, StringComparison.Ordinal)
            && (LevelCall.LevelOfProperty(property.Name) is not null || string.Equals(property.Name, "Category", StringComparison.Ordinal)))
            return null;

        if (whole.Parent is ObjectCreationExpressionSyntax creation && symbol is INamedTypeSymbol type && (IsBuilder(type) || IsBridge(type)))
            return null;

        return whole;
    }

    private static string Describe(ISymbol symbol) =>
        symbol is IMethodSymbol method ? (method.ReducedFrom ?? method).ContainingType.Name + "." + method.Name : symbol.Name;

    private static System.Collections.Generic.List<(SyntaxNode Node, DiagnosticDescriptor Rule, string Argument)> TypeChangingSites(SyntaxNode root, SemanticModel model, System.Threading.CancellationToken cancellationToken)
    {
        var sites = new System.Collections.Generic.List<(SyntaxNode, DiagnosticDescriptor, string)>();
        foreach (var node in root.DescendantNodes())
        {
            if (node is InvocationExpressionSyntax && model.GetOperation(node, cancellationToken) is IInvocationOperation invocation)
            {
                if (Acquisition.IsLocatorGetLogger(invocation))
                    sites.Add((node, Rules.FactoryLocator, "WatchFactoryLocator.Factory.GetLogger"));
                else if (Acquisition.IsSlim(invocation))
                    sites.Add((node, Rules.ObjectExtension, "EnterMethodSlim"));
            }
            else if (node is QualifiedNameSyntax or MemberAccessExpressionSyntax && ReferenceEquals(QualifiedRule(node, model, cancellationToken), Rules.LoggerType))
            {
                sites.Add((node, Rules.LoggerType, node.ToString()));
            }
        }

        return sites;
    }
}
