// Copyright (c) Pond Hawk Technologies Inc. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Pondhawk.Logging.Migration;

/// <summary>
/// Fabrica.Watch's <c>ILogger</c> in the signature of a member other files can see — a base class's
/// <c>protected ILogger EnterMethod()</c>, a <c>public ILogger Logger { get; }</c>, an <c>internal</c> helper's
/// <c>ILogger</c> parameter — which PHLM017 retypes: to <c>MethodLogger</c> for a method that enters a method
/// trace, since its callers dispose it, and to Microsoft.Extensions.Logging's <c>ILogger</c> otherwise.
/// </summary>
internal static class HandedOut
{
    /// <summary>Each simple <c>ILogger</c> naming Fabrica.Watch's in a non-private member's signature.</summary>
    public static List<SimpleNameSyntax> Find(SyntaxNode root, SemanticModel model, CancellationToken cancellationToken) =>
        root.DescendantNodes().OfType<MemberDeclarationSyntax>()
            .Where(m => m is not BaseTypeDeclarationSyntax and not BaseNamespaceDeclarationSyntax and not DelegateDeclarationSyntax)
            .Where(m => Member(m, model, cancellationToken) is { DeclaredAccessibility: not Accessibility.Private } member && !ImplementsFabrica(member))
            .SelectMany(SignatureTypes)
            .SelectMany(t => t.DescendantNodesAndSelf().OfType<SimpleNameSyntax>())
            .Where(n => n.Parent is not QualifiedNameSyntax { Right: var right } || right != n)
            .Where(n => string.Equals(FabricaWatch.NameOf(model.GetSymbolInfo(n, cancellationToken).Symbol as ITypeSymbol), FabricaWatch.Logger, StringComparison.Ordinal))
            .ToList();

    /// <summary>The member whose signature <paramref name="name"/> is in.</summary>
    public static ISymbol? MemberOf(SimpleNameSyntax name, SemanticModel model, CancellationToken cancellationToken) =>
        name.FirstAncestorOrSelf<MemberDeclarationSyntax>() is { } member ? Member(member, model, cancellationToken) : null;

    /// <summary>
    /// Whether the member hands a logger out through this name — a return type, a property's or field's type, an
    /// out or ref parameter — rather than taking one in. Retyping one of those changes the type under the files
    /// that call it, so it waits until their calls are migrated.
    /// </summary>
    public static bool GivesOut(SimpleNameSyntax name) =>
        name.FirstAncestorOrSelf<ParameterSyntax>() is not { } parameter
        || parameter.Modifiers.Any(m => m.ValueText is "out" or "ref");

    /// <summary>The type to write in place of <paramref name="name"/>, fully qualified.</summary>
    public static string Replacement(SimpleNameSyntax name) =>
        EntersMethod(name) ? Rewrites.Pondhawk + "MethodLogger" : Rewrites.Logging + "ILogger";

    // The method's return type, and the method enters a trace: it is called EnterMethod…, or what it returns is an
    // EnterMethod call. Its callers write using var logger = EnterMethod(); and a MethodLogger is what they dispose.
    private static bool EntersMethod(SimpleNameSyntax name)
    {
        var type = name.Parent is NullableTypeSyntax nullable ? nullable : (TypeSyntax)name;
        if (type.Parent is not MethodDeclarationSyntax method || method.ReturnType != type)
            return false;

        if (method.Identifier.ValueText.StartsWith("EnterMethod", StringComparison.Ordinal))
            return true;

        List<ExpressionSyntax?> returned = method.ExpressionBody is { } arrow
            ? [arrow.Expression]
            : method.Body?.DescendantNodes(n => n is not AnonymousFunctionExpressionSyntax and not LocalFunctionStatementSyntax)
                .OfType<ReturnStatementSyntax>().Select(r => r.Expression).ToList() ?? [];

        return returned.Count > 0 && returned.All(e => e is InvocationExpressionSyntax { Expression: var called } && Called(called) is "EnterMethod");
    }

    private static string? Called(ExpressionSyntax expression) =>
        expression switch
        {
            IdentifierNameSyntax name => name.Identifier.ValueText,
            GenericNameSyntax generic => generic.Identifier.ValueText,
            MemberAccessExpressionSyntax access => access.Name.Identifier.ValueText,
            _ => null,
        };

    private static ISymbol? Member(MemberDeclarationSyntax member, SemanticModel model, CancellationToken cancellationToken) =>
        member is BaseFieldDeclarationSyntax field
            ? field.Declaration.Variables.Select(v => model.GetDeclaredSymbol(v, cancellationToken)).FirstOrDefault()
            : model.GetDeclaredSymbol(member, cancellationToken);

    // A member that implements or overrides Fabrica.Watch's own keeps its type: it is set-up, changed by hand.
    private static bool ImplementsFabrica(ISymbol member)
    {
        if (member is IMethodSymbol { OverriddenMethod: { } overridden } && FabricaWatch.Owns(overridden))
            return true;
        if (member is IPropertySymbol { OverriddenProperty: { } property } && FabricaWatch.Owns(property))
            return true;

        return member.ContainingType?.AllInterfaces
            .Where(i => FabricaWatch.Owns(i))
            .SelectMany(i => i.GetMembers())
            .Any(m => SymbolEqualityComparer.Default.Equals(member.ContainingType.FindImplementationForInterfaceMember(m), member)) ?? false;
    }

    private static IEnumerable<TypeSyntax> SignatureTypes(MemberDeclarationSyntax member)
    {
        List<TypeSyntax> types = member switch
        {
            MethodDeclarationSyntax method => [method.ReturnType],
            OperatorDeclarationSyntax op => [op.ReturnType],
            ConversionOperatorDeclarationSyntax conversion => [conversion.Type],
            BasePropertyDeclarationSyntax property => [property.Type],
            BaseFieldDeclarationSyntax field => [field.Declaration.Type],
            _ => [],
        };

        var parameters = member switch
        {
            BaseMethodDeclarationSyntax method => method.ParameterList.Parameters,
            IndexerDeclarationSyntax indexer => indexer.ParameterList.Parameters,
            _ => default,
        };

        return types.Concat(parameters.Select(p => p.Type).OfType<TypeSyntax>());
    }
}
