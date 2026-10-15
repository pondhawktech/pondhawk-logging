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
/// A Fabrica.Watch level call PHLM001 rewrites — <c>Trace/Debug/Info/Warning/Error(message)</c>,
/// <c>(Exception, message)</c>, or a template overload called with no arguments. A template with arguments
/// (PHLM002), an interpolated or concatenated message (PHLM003) and a <c>Func&lt;string&gt;</c> (PHLM004) are
/// left to their own rules.
/// </summary>
internal sealed class LevelCall
{
    private static readonly (string Fabrica, string Method, string Level)[] Levels =
    [
        ("Trace", "LogTrace", "Trace"),
        ("Debug", "LogDebug", "Debug"),
        ("Info", "LogInformation", "Information"),
        ("Warning", "LogWarning", "Warning"),
        ("Error", "LogError", "Error"),
    ];

    private LevelCall(string name, string method, ExpressionSyntax? exception, ExpressionSyntax? message, MessageKind kind)
    {
        Name = name;
        Method = method;
        Exception = exception;
        Message = message;
        Kind = kind;
    }

    /// <summary>The Fabrica.Watch method called.</summary>
    public string Name { get; }

    /// <summary>The Microsoft.Extensions.Logging method it becomes.</summary>
    public string Method { get; }

    /// <summary>The exception passed, if any.</summary>
    public ExpressionSyntax? Exception { get; }

    /// <summary>The message passed, if one was written.</summary>
    public ExpressionSyntax? Message { get; }

    /// <summary>What the message is.</summary>
    public MessageKind Kind { get; }

    /// <summary>The level method Fabrica.Watch's level property name stands for, e.g. <c>IsInfoEnabled</c> → <c>Information</c>.</summary>
    public static string? LevelOfProperty(string property)
    {
        foreach (var (fabrica, _, level) in Levels)
        {
            if (string.Equals(property, "Is" + fabrica + "Enabled", StringComparison.Ordinal))
                return level;
        }

        return null;
    }

    /// <summary>The call PHLM001 rewrites, or null when this invocation is not one.</summary>
    public static LevelCall? Match(IInvocationOperation invocation)
    {
        var declared = FabricaWatch.Declared(invocation);
        var level = Levels.FirstOrDefault(l => string.Equals(l.Fabrica, declared.Name, StringComparison.Ordinal));
        if (level.Fabrica is null || !FabricaWatch.Calls(invocation, FabricaWatch.LoggerExtensions, declared.Name))
            return null;

        var parameters = declared.Parameters;
        var exceptionFirst = parameters.Length > 2 && IsException(parameters[1].Type);
        var messageAt = exceptionFirst ? 2 : 1;
        if (parameters.Length <= messageAt)
            return null;

        var message = parameters[messageAt];
        var isTemplate = message.Type.SpecialType == SpecialType.System_String
            && parameters.Length > messageAt + 1 && parameters[messageAt + 1].IsParams;

        if (message.Type.SpecialType != SpecialType.System_Object && !isTemplate)
            return null;   // Func<string> — PHLM004

        if (isTemplate && !HasNoArguments(FabricaWatch.Argument(invocation, messageAt + 1)))
            return null;   // a template with arguments — PHLM002

        var argument = FabricaWatch.Argument(invocation, messageAt);
        var written = FabricaWatch.Written(argument);
        if (written is not null && IsBuiltFromParts(written, argument!))
            return null;   // interpolated or concatenated — PHLM003

        var exception = exceptionFirst ? FabricaWatch.Written(FabricaWatch.Argument(invocation, 1)) : null;
        return new LevelCall(level.Fabrica, level.Method, exception, written, KindOf(argument));
    }

    private static bool IsException(ITypeSymbol type) =>
        string.Equals(type.ToDisplayString(), "System.Exception", StringComparison.Ordinal);

    // The params array of a template overload, called with no elements.
    private static bool HasNoArguments(IArgumentOperation? argument) =>
        argument is { ArgumentKind: ArgumentKind.ParamArray, Value: IArrayCreationOperation creation }
        && (creation.Initializer?.ElementValues.Length ?? 0) == 0;

    private static bool IsBuiltFromParts(ExpressionSyntax written, IArgumentOperation argument)
    {
        var expression = written;
        while (expression is ParenthesizedExpressionSyntax parenthesized)
            expression = parenthesized.Expression;

        return expression.IsKind(SyntaxKind.InterpolatedStringExpression)
            || (expression.IsKind(SyntaxKind.AddExpression) && !Unwrapped(argument).ConstantValue.HasValue
                && Unwrapped(argument).Type?.SpecialType == SpecialType.System_String);
    }

    private static MessageKind KindOf(IArgumentOperation? argument)
    {
        if (argument is null || argument.ArgumentKind != ArgumentKind.Explicit)
            return MessageKind.None;

        var value = Unwrapped(argument);
        if (value.ConstantValue.HasValue)
            return value.ConstantValue.Value is string ? MessageKind.Constant : value.ConstantValue.Value is null ? MessageKind.None : MessageKind.Value;

        return value.Type?.SpecialType == SpecialType.System_String ? MessageKind.Text : MessageKind.Value;
    }

    private static IOperation Unwrapped(IArgumentOperation argument) =>
        argument.Value is IConversionOperation { IsImplicit: true } conversion ? conversion.Operand : argument.Value;
}
