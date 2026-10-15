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
/// Finds Fabrica.Watch logging code that has a Microsoft.Extensions.Logging / Pondhawk.Logging equivalent, so
/// <see cref="FabricaWatchCodeFixProvider"/> can rewrite it.
/// </summary>
[DiagnosticAnalyzer(LanguageNames.CSharp)]
public sealed class FabricaWatchAnalyzer : DiagnosticAnalyzer
{
    /// <inheritdoc />
    public override ImmutableArray<DiagnosticDescriptor> SupportedDiagnostics { get; } = ImmutableArray.Create(
        Rules.LevelCall,
        Rules.LevelEnabled,
        Rules.FactoryLocator,
        Rules.ObjectExtension,
        Rules.SharedType,
        Rules.Usings,
        Rules.LoggerType);

    /// <inheritdoc />
    public override void Initialize(AnalysisContext context)
    {
        context.ConfigureGeneratedCodeAnalysis(GeneratedCodeAnalysisFlags.None);
        context.EnableConcurrentExecution();

        context.RegisterOperationAction(AnalyzeInvocation, OperationKind.Invocation);
        context.RegisterOperationAction(AnalyzePropertyReference, OperationKind.PropertyReference);
        context.RegisterSyntaxNodeAction(AnalyzeQualifiedName, SyntaxKind.QualifiedName, SyntaxKind.SimpleMemberAccessExpression);
        context.RegisterSemanticModelAction(AnalyzeUsings);
    }

    private static void AnalyzeInvocation(OperationAnalysisContext context)
    {
        var invocation = (IInvocationOperation)context.Operation;
        if (!FabricaWatch.Owns(invocation.TargetMethod) || FabricaWatch.Owns(context.ContainingSymbol))
            return;

        var location = invocation.Syntax.GetLocation();

        if (LevelCall.Match(invocation) is { } call)
            context.ReportDiagnostic(Diagnostic.Create(Rules.LevelCall, location, call.Name, call.Method));
        else if (Acquisition.IsLocatorGetLogger(invocation))
            context.ReportDiagnostic(Diagnostic.Create(Rules.FactoryLocator, location, "WatchFactoryLocator.Factory.GetLogger"));
        else if (Acquisition.NeedsObjectRewrite(invocation))
            context.ReportDiagnostic(Diagnostic.Create(Rules.ObjectExtension, location, FabricaWatch.Declared(invocation).Name));
    }

    private static void AnalyzePropertyReference(OperationAnalysisContext context)
    {
        var reference = (IPropertyReferenceOperation)context.Operation;
        if (FabricaWatch.Owns(context.ContainingSymbol)
            || reference.Syntax is not MemberAccessExpressionSyntax
            || !string.Equals(FabricaWatch.NameOf(reference.Property.ContainingType), FabricaWatch.Logger, StringComparison.Ordinal)
            || LevelCall.LevelOfProperty(reference.Property.Name) is not { } level)
            return;

        context.ReportDiagnostic(Diagnostic.Create(Rules.LevelEnabled, reference.Syntax.GetLocation(), reference.Property.Name, level));
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

            var info = context.SemanticModel.GetSymbolInfo(name, context.CancellationToken);
            var symbol = info.Symbol ?? info.CandidateSymbols.FirstOrDefault();
            if (FabricaWatch.Owns(symbol) && !CarriedOver.IsCarried(name, symbol!, context.SemanticModel, context.CancellationToken))
                return;
        }

        foreach (var directive in usings)
            context.ReportDiagnostic(Diagnostic.Create(Rules.Usings, directive.GetLocation(), directive.Name!.ToString()));
    }
}
