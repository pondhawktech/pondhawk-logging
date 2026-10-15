// Copyright (c) Pond Hawk Technologies Inc. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Syntax;
using Microsoft.CodeAnalysis.Operations;

namespace Pondhawk.Logging.Migration;

/// <summary>The ways Fabrica.Watch hands out a logger that PHLM012 and PHLM013 rewrite.</summary>
internal static class Acquisition
{
    /// <summary>
    /// <c>WatchFactoryLocator.Factory.GetLogger(category)</c>, <c>GetLogger&lt;T&gt;()</c> or <c>GetLogger(Type)</c> —
    /// not the <c>LoggerRequest</c> overload, which has no equivalent.
    /// </summary>
    public static bool IsLocatorGetLogger(IInvocationOperation invocation)
    {
        var declared = FabricaWatch.Declared(invocation);
        if (!string.Equals(declared.Name, "GetLogger", StringComparison.Ordinal)
            || !string.Equals(FabricaWatch.NameOf(declared.ContainingType), FabricaWatch.WatchFactory, StringComparison.Ordinal))
            return false;

        if (invocation.Instance is not IPropertyReferenceOperation { Property: { Name: "Factory" } factory }
            || !string.Equals(FabricaWatch.NameOf(factory.ContainingType), FabricaWatch.WatchFactoryLocator, StringComparison.Ordinal))
            return false;

        var first = declared.Parameters.FirstOrDefault();
        return declared.IsGenericMethod
            || first?.Type.SpecialType == SpecialType.System_String
            || string.Equals(first?.Type.ToDisplayString(), "System.Type", StringComparison.Ordinal);
    }

    /// <summary>
    /// A <c>WatchExtensions</c> call whose text must change: <c>EnterMethodSlim(category)</c>, or
    /// <c>EnterMethod</c> with explicit type arguments or a named argument. A plain <c>this.GetLogger()</c> or
    /// <c>this.EnterMethod()</c> needs no change — the using swap carries it over.
    /// </summary>
    public static bool NeedsObjectRewrite(IInvocationOperation invocation)
    {
        if (FabricaWatch.Calls(invocation, FabricaWatch.WatchExtensions, "EnterMethodSlim"))
            return true;

        return FabricaWatch.Calls(invocation, FabricaWatch.WatchExtensions, "EnterMethod")
            && !IsPlain((InvocationExpressionSyntax)invocation.Syntax);
    }

    /// <summary><c>this.EnterMethodSlim(category)</c>, which becomes a Microsoft.Extensions.Logging logger.</summary>
    public static bool IsSlim(IInvocationOperation invocation) =>
        FabricaWatch.Calls(invocation, FabricaWatch.WatchExtensions, "EnterMethodSlim");

    /// <summary>Whether a call is written with no type arguments and no named arguments.</summary>
    public static bool IsPlain(InvocationExpressionSyntax invocation) =>
        invocation.Expression is MemberAccessExpressionSyntax { Name: IdentifierNameSyntax }
        && invocation.ArgumentList.Arguments.All(a => a.NameColon is null);
}
