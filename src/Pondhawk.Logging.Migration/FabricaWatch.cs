// Copyright (c) Pond Hawk Technologies Inc. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;

namespace Pondhawk.Logging.Migration;

/// <summary>
/// What the rules know of Fabrica.Watch. Symbols are recognised by name, never by assembly, so the analyzer
/// works on any copy of Fabrica.Watch, vendored or packaged.
/// </summary>
internal static class FabricaWatch
{
    public const string Namespace = "Fabrica.Watch";
    public const string Logger = "Fabrica.Watch.ILogger";
    public const string LoggerExtensions = "Fabrica.Watch.LoggerExtensions";
    public const string WatchExtensions = "Fabrica.Watch.WatchExtensions";
    public const string WatchFactory = "Fabrica.Watch.IWatchFactory";
    public const string WatchFactoryLocator = "Fabrica.Watch.WatchFactoryLocator";
    public const string Sensitive = "Fabrica.Watch.SensitiveAttribute";
    public const string PayloadType = "Fabrica.Watch.Sink.PayloadType";

    /// <summary>A type's namespace-qualified name, without generic arguments.</summary>
    public static string NameOf(ITypeSymbol? type) =>
        type is null || type.ContainingNamespace is null ? string.Empty : type.ContainingNamespace.ToDisplayString() + "." + type.MetadataName;

    /// <summary>Whether a symbol is Fabrica.Watch's — a namespace under it, or something declared in one.</summary>
    public static bool Owns(ISymbol? symbol)
    {
        if (symbol is IMethodSymbol { ReducedFrom: { } reduced })
            symbol = reduced;

        var ns = symbol as INamespaceSymbol ?? symbol?.ContainingNamespace;
        return IsFabricaNamespace(ns?.ToDisplayString());
    }

    /// <summary>Whether a namespace name is Fabrica.Watch or under it.</summary>
    public static bool IsFabricaNamespace(string? name) =>
        string.Equals(name, Namespace, StringComparison.Ordinal) || (name?.StartsWith(Namespace + ".", StringComparison.Ordinal) ?? false);

    /// <summary>Whether a using directive imports a Fabrica.Watch namespace (not an alias, not static, not global).</summary>
    public static bool IsFabricaUsing(UsingDirectiveSyntax directive) =>
        directive.Alias is null
        && directive.StaticKeyword.IsKind(SyntaxKind.None)
        && directive.GlobalKeyword.IsKind(SyntaxKind.None)
        && IsFabricaNamespace(directive.Name?.ToString());

    /// <summary>The method an invocation calls, as declared — a reduced extension method's original.</summary>
    public static IMethodSymbol Declared(IInvocationOperation invocation) =>
        invocation.TargetMethod.ReducedFrom ?? invocation.TargetMethod;

    /// <summary>
    /// Whether the invocation calls <paramref name="type"/>.<paramref name="name"/> through instance syntax
    /// (<c>x.Name(…)</c> or <c>x?.Name(…)</c>) — not an extension method called as a static one, whose receiver is
    /// an argument.
    /// </summary>
    public static bool Calls(IInvocationOperation invocation, string type, string name) =>
        string.Equals(Declared(invocation).Name, name, StringComparison.Ordinal)
        && string.Equals(NameOf(Declared(invocation).ContainingType), type, StringComparison.Ordinal)
        && invocation.Syntax is InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax or MemberBindingExpressionSyntax }
        && !(Declared(invocation).IsExtensionMethod && Argument(invocation, 0)?.Syntax is ArgumentSyntax);

    /// <summary>The argument passed for the parameter at <paramref name="ordinal"/> of the declared method, if any.</summary>
    public static IArgumentOperation? Argument(IInvocationOperation invocation, int ordinal) =>
        invocation.Arguments.FirstOrDefault(a => a.Parameter?.Ordinal == ordinal);

    /// <summary>The syntax written for an argument, or null when the argument was left to its default.</summary>
    public static ExpressionSyntax? Written(IArgumentOperation? argument) =>
        argument is { ArgumentKind: ArgumentKind.Explicit, Syntax: ArgumentSyntax syntax } ? syntax.Expression : null;

    /// <summary>Whether a name is written qualified by a namespace (<c>Fabrica.Watch.ILogger</c>), not as a simple name.</summary>
    public static bool IsQualifiedByNamespace(SyntaxNode node, SemanticModel model) =>
        node switch
        {
            QualifiedNameSyntax q => model.GetSymbolInfo(q.Left).Symbol is INamespaceSymbol,
            MemberAccessExpressionSyntax m => model.GetSymbolInfo(m.Expression).Symbol is INamespaceSymbol,
            _ => false,
        };

    /// <summary>The name syntax that names a type: the outermost qualified form when the simple name is its right part.</summary>
    public static SyntaxNode Whole(SimpleNameSyntax name) =>
        name.Parent switch
        {
            QualifiedNameSyntax q when q.Right == name => q,
            MemberAccessExpressionSyntax m when m.Name == name => m,
            AliasQualifiedNameSyntax a when a.Name == name => a,
            _ => name,
        };
}
