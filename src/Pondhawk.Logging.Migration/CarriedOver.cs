// Copyright (c) Pond Hawk Technologies Inc. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System;
using System.Linq;
using System.Threading;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;

namespace Pondhawk.Logging.Migration;

/// <summary>
/// Which Fabrica.Watch uses the using swap (PHLM015) carries over unchanged: written the same way, they mean the
/// same thing once <c>using Microsoft.Extensions.Logging;</c> and <c>using Pondhawk.Logging;</c> replace
/// <c>using Fabrica.Watch;</c>.
/// </summary>
internal static class CarriedOver
{
    private static readonly string[] Types = [FabricaWatch.Logger, FabricaWatch.Sensitive, FabricaWatch.PayloadType];

    // Fabrica.Watch logger extensions whose Pondhawk.Logging namesake takes the same arguments.
    private static readonly string[] SameShape = ["LogSql", "LogYaml", "LogObject"];

    /// <summary>Whether this Fabrica.Watch use needs nothing but the using swap.</summary>
    public static bool IsCarried(SimpleNameSyntax name, ISymbol symbol, SemanticModel model, CancellationToken cancellationToken)
    {
        switch (symbol)
        {
            case INamedTypeSymbol type:
                return Types.Contains(FabricaWatch.NameOf(type), StringComparer.Ordinal) && IsSimple(name, model);

            case IMethodSymbol { MethodKind: MethodKind.Constructor } constructor:
                return string.Equals(FabricaWatch.NameOf(constructor.ContainingType), FabricaWatch.Sensitive, StringComparison.Ordinal) && IsSimple(name, model);

            case IFieldSymbol field:
                return string.Equals(FabricaWatch.NameOf(field.ContainingType), FabricaWatch.PayloadType, StringComparison.Ordinal);

            case IMethodSymbol:
                return name.Parent is MemberAccessExpressionSyntax { Parent: InvocationExpressionSyntax call } access
                    && access.Name == name
                    && model.GetOperation(call, cancellationToken) is IInvocationOperation invocation
                    && IsCarriedCall(invocation);

            default:
                return false;
        }
    }

    private static bool IsSimple(SimpleNameSyntax name, SemanticModel model) =>
        !FabricaWatch.IsQualifiedByNamespace(FabricaWatch.Whole(name), model);

    private static bool IsCarriedCall(IInvocationOperation invocation)
    {
        var declared = FabricaWatch.Declared(invocation);
        var syntax = (InvocationExpressionSyntax)invocation.Syntax;
        var container = FabricaWatch.NameOf(declared.ContainingType);

        if (string.Equals(container, FabricaWatch.WatchExtensions, StringComparison.Ordinal))
        {
            return string.Equals(declared.Name, "GetLogger", StringComparison.Ordinal)
                || (string.Equals(declared.Name, "EnterMethod", StringComparison.Ordinal) && Acquisition.IsPlain(syntax));
        }

        if (!string.Equals(container, FabricaWatch.LoggerExtensions, StringComparison.Ordinal))
            return false;

        return declared.Name switch
        {
            "LogJson" or "LogXml" => FabricaWatch.Argument(invocation, 3) is not { ArgumentKind: ArgumentKind.Explicit },
            "Inspect" => HasStringName(invocation),
            "ErrorWithContext" => HasStringMessage(invocation),
            _ => SameShape.Contains(declared.Name, StringComparer.Ordinal),
        };
    }

    /// <summary>Fabrica.Watch's Inspect takes an object name; Pondhawk.Logging's a string.</summary>
    public static bool HasStringName(IInvocationOperation invocation) =>
        FabricaWatch.Argument(invocation, 1) is { } argument && IsString(argument);

    /// <summary>Fabrica.Watch's ErrorWithContext message is optional and any object; Pondhawk.Logging's a string.</summary>
    public static bool HasStringMessage(IInvocationOperation invocation) =>
        FabricaWatch.Argument(invocation, 3) is { ArgumentKind: ArgumentKind.Explicit } argument && IsString(argument);

    private static bool IsString(IArgumentOperation argument)
    {
        var value = argument.Value is IConversionOperation { IsImplicit: true } conversion ? conversion.Operand : argument.Value;
        return value.Type?.SpecialType == SpecialType.System_String;
    }
}
