// Copyright (c) Pond Hawk Technologies Inc. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System;
using System.Linq;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Pondhawk.Logging.Migration;

/// <summary>
/// A <c>using</c> on a Fabrica.Watch logger that is not an <c>EnterMethod</c> (PHLM006), as a declaration
/// (<c>using var logger = this.GetLogger();</c>) or a statement (<c>using (var logger = …) { … }</c>). Every
/// Fabrica.Watch logger is disposable; a Microsoft.Extensions.Logging one is not, and only a method trace has
/// anything to close.
/// </summary>
internal static class Disposal
{
    /// <summary>The declaration a <c>using</c> declaration or statement disposes, or null.</summary>
    public static VariableDeclarationSyntax? Disposed(SyntaxNode node) =>
        node switch
        {
            LocalDeclarationStatementSyntax local when !local.UsingKeyword.IsKind(SyntaxKind.None) => local.Declaration,
            UsingStatementSyntax statement => statement.Declaration,
            _ => null,
        };

    /// <summary>Whether this <c>using</c> has nothing left to dispose once the logger is migrated.</summary>
    public static bool IsNeedless(SyntaxNode node, SemanticModel model, CancellationToken cancellationToken)
    {
        if (Disposed(node) is not { Variables.Count: 1 } declaration || declaration.Variables[0].Initializer?.Value is not { } value)
            return false;

        if (!string.Equals(FabricaWatch.NameOf(model.GetTypeInfo(value, cancellationToken).Type), FabricaWatch.Logger, StringComparison.Ordinal))
            return false;

        var called = value is InvocationExpressionSyntax invocation
            ? invocation.Expression switch
            {
                MemberAccessExpressionSyntax access => access.Name.Identifier.ValueText,
                SimpleNameSyntax simple => simple.Identifier.ValueText,
                _ => null,
            }
            : null;

        return called is not ("EnterMethod" or "EnterMethodSlim");
    }

    /// <summary>
    /// <c>logger.Dispose();</c> on a Fabrica.Watch logger that is not a method trace — a plain logger handed back to
    /// Fabrica.Watch's pool. A Microsoft.Extensions.Logging logger has no Dispose, and nothing to give back.
    /// </summary>
    public static bool IsNeedlessDispose(ExpressionStatementSyntax statement, SemanticModel model, CancellationToken cancellationToken)
    {
        if (statement.Expression is not InvocationExpressionSyntax { Expression: MemberAccessExpressionSyntax { Name.Identifier.ValueText: "Dispose" } access, ArgumentList.Arguments.Count: 0 }
            || !string.Equals(FabricaWatch.NameOf(model.GetTypeInfo(access.Expression, cancellationToken).Type), FabricaWatch.Logger, StringComparison.Ordinal))
            return false;

        // A local from an EnterMethod is a method trace: disposing it ends the trace, and a MethodLogger can.
        return model.GetSymbolInfo(access.Expression, cancellationToken).Symbol is not ILocalSymbol local
            || local.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax(cancellationToken) is not VariableDeclaratorSyntax { Initializer.Value: InvocationExpressionSyntax creation }
            || (creation.Expression switch
            {
                MemberAccessExpressionSyntax m => m.Name.Identifier.ValueText,
                SimpleNameSyntax n => n.Identifier.ValueText,
                _ => null,
            }) is not ("EnterMethod" or "EnterMethodSlim");
    }
}
