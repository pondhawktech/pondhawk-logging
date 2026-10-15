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
        Rules.ScopeState);

    /// <inheritdoc />
    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();

        context.RegisterOperationAction(AnalyzeInvocation, OperationKind.Invocation);
        context.RegisterOperationAction(AnalyzeCreation, OperationKind.ObjectCreation);
        context.RegisterOperationAction(AnalyzePropertyReference, OperationKind.PropertyReference);
        context.RegisterSyntaxNodeAction(AnalyzeQualifiedName, SyntaxKind.QualifiedName, SyntaxKind.SimpleMemberAccessExpression);
        context.RegisterSemanticModelAction(AnalyzeUsings);
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

        var name = FabricaWatch.Declared(invocation).Name;
        if (Acquisition.IsLocatorGetLogger(invocation))
            return (Rules.FactoryLocator, ["WatchFactoryLocator.Factory.GetLogger"]);
        if (Acquisition.NeedsObjectRewrite(invocation))
            return (Rules.ObjectExtension, [name]);

        return (LoggerExtension(invocation, name) ?? ReportOnly(invocation, name), [name]);
    }

    // The rest of Fabrica.Watch's logger extensions, called as extensions.
    private static DiagnosticDescriptor? LoggerExtension(IInvocationOperation invocation, string name)
    {
        if (!FabricaWatch.Calls(invocation, FabricaWatch.LoggerExtensions, name))
            return null;

        return name switch
        {
            "EnterMethod" when invocation.Syntax.Parent is ExpressionStatementSyntax => Rules.MethodTrace,
            "LeaveMethod" => Rules.MethodTrace,
            "EnterScope" or "LeaveScope" => Rules.Scope,
            "ErrorWithContext" when !CarriedOver.HasStringMessage(invocation) => Rules.ContextCall,
            "WarningWithContext" => Rules.ContextCall,
            "Inspect" when !CarriedOver.HasStringName(invocation) => Rules.InspectName,
            "LogObject" => Rules.ObjectLevel,
            "LogJson" or "LogXml" when FabricaWatch.Argument(invocation, 3) is { ArgumentKind: ArgumentKind.Explicit } => Rules.Pretty,
            _ => null,
        };
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

    // Fabrica.Watch.ILogger, Fabrica.Watch.SensitiveAttribute and Fabrica.Watch.Sink.PayloadType written out in
    // full. A simple name needs no rewrite: the using swap carries it over.
    private static void AnalyzeQualifiedName(SyntaxNodeAnalysisContext context)
    {
        var node = context.Node;
        if (FabricaWatch.Owns(context.ContainingSymbol))
            return;   // Fabrica.Watch's own source, vendored into the solution

        if (node.Parent is QualifiedNameSyntax { Left: var left } && left == node)
            return;   // part of a longer name; that one is looked at instead

        if (!FabricaWatch.IsQualifiedByNamespace(node, context.SemanticModel))
            return;

        var symbol = context.SemanticModel.GetSymbolInfo(node, context.CancellationToken).Symbol;
        var type = symbol as INamedTypeSymbol ?? (symbol as IMethodSymbol is { MethodKind: MethodKind.Constructor } constructor ? constructor.ContainingType : null);

        var rule = FabricaWatch.NameOf(type) switch
        {
            FabricaWatch.Logger => Rules.LoggerType,
            FabricaWatch.Sensitive or FabricaWatch.PayloadType => Rules.SharedType,
            _ => null,
        };

        if (rule is not null)
            context.ReportDiagnostic(Diagnostic.Create(rule, node.GetLocation(), node.ToString()));
    }

    // Once every Fabrica.Watch use left in a file is one the using swap carries over, the usings can go.
    private static void AnalyzeUsings(SemanticModelAnalysisContext context)
    {
        var root = context.SemanticModel.SyntaxTree.GetRoot(context.CancellationToken);
        var usings = root.DescendantNodes().OfType<UsingDirectiveSyntax>().Where(FabricaWatch.IsFabricaUsing).ToList();
        if (usings.Count == 0
            || root.DescendantNodes().OfType<BaseNamespaceDeclarationSyntax>().Any(n => FabricaWatch.IsFabricaNamespace(n.Name.ToString())))
            return;

        foreach (var name in root.DescendantNodes().OfType<SimpleNameSyntax>())
        {
            if (name.FirstAncestorOrSelf<UsingDirectiveSyntax>() is not null)
                continue;

            // Only what binds counts. A call that fails to bind against Fabrica.Watch is one a rewrite wrote for
            // the new API (logger.LogObject(LogLevel.Warning, …) on a logger still typed as Fabrica.Watch's).
            var symbol = context.SemanticModel.GetSymbolInfo(name, context.CancellationToken).Symbol;
            if (FabricaWatch.Owns(symbol) && !CarriedOver.IsCarried(name, symbol!, context.SemanticModel, context.CancellationToken))
                return;
        }

        foreach (var directive in usings)
            context.ReportDiagnostic(Diagnostic.Create(Rules.Usings, directive.GetLocation(), directive.Name!.ToString()));
    }
}
