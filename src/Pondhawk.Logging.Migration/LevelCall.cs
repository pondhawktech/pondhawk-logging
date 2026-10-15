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
/// A Fabrica.Watch level call — <c>Trace/Debug/Info/Warning/Error</c> on a logger — and which of its shapes it
/// is: a plain message (PHLM001), a template with arguments (PHLM002), a message built from parts (PHLM003)
/// or a lazy <c>Func&lt;string&gt;</c> (PHLM004).
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

    private LevelCall((string Fabrica, string Method, string Level) level, LevelShape shape, ExpressionSyntax? exception, IArgumentOperation? message, IArgumentOperation? arguments)
    {
        Name = level.Fabrica;
        Method = level.Method;
        Level = level.Level;
        Shape = shape;
        Exception = exception;
        MessageArgument = message;
        Message = FabricaWatch.Written(message);
        Arguments = arguments;
    }

    /// <summary>The Fabrica.Watch method called.</summary>
    public string Name { get; }

    /// <summary>The Microsoft.Extensions.Logging method it becomes.</summary>
    public string Method { get; }

    /// <summary>The <c>LogLevel</c> member it logs at.</summary>
    public string Level { get; }

    /// <summary>Which rule's shape the call is.</summary>
    public LevelShape Shape { get; }

    /// <summary>The exception passed, if any.</summary>
    public ExpressionSyntax? Exception { get; }

    /// <summary>The message (or template, or <c>Func&lt;string&gt;</c>) passed, if one was written.</summary>
    public ExpressionSyntax? Message { get; }

    /// <summary>The message argument's operation.</summary>
    public IArgumentOperation? MessageArgument { get; }

    /// <summary>The <c>params</c> argument of a template call.</summary>
    public IArgumentOperation? Arguments { get; }

    /// <summary>What a plain message is.</summary>
    public MessageKind Kind => KindOf(MessageArgument);

    /// <summary>The level Fabrica.Watch's level property stands for, e.g. <c>IsInfoEnabled</c> → <c>Information</c>.</summary>
    public static string? LevelOfProperty(string property)
    {
        foreach (var (fabrica, _, level) in Levels)
        {
            if (string.Equals(property, "Is" + fabrica + "Enabled", StringComparison.Ordinal))
                return level;
        }

        return null;
    }

    /// <summary>The level call this invocation is, or null when it is not one.</summary>
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

        var message = FabricaWatch.Argument(invocation, messageAt);
        var exception = exceptionFirst ? FabricaWatch.Written(FabricaWatch.Argument(invocation, 1)) : null;
        var type = parameters[messageAt].Type;

        if (type is INamedTypeSymbol { Name: "Func", Arity: 1 })
            return new LevelCall(level, LevelShape.Lazy, exception, message, null);

        var isTemplate = type.SpecialType == SpecialType.System_String && parameters.Length > messageAt + 1 && parameters[messageAt + 1].IsParams;
        var arguments = isTemplate ? FabricaWatch.Argument(invocation, messageAt + 1) : null;
        if (isTemplate && !HasNoArguments(arguments))
            return new LevelCall(level, LevelShape.Template, exception, message, arguments);

        var written = FabricaWatch.Written(message);
        var shape = written is not null && IsBuiltFromParts(written, message!) ? LevelShape.Parts : LevelShape.Simple;
        return new LevelCall(level, shape, exception, message, null);
    }

    /// <summary>The value an argument passes, without the implicit conversion to the parameter's type.</summary>
    public static IOperation Unwrapped(IArgumentOperation argument) =>
        argument.Value is IConversionOperation { IsImplicit: true } conversion ? conversion.Operand : argument.Value;

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

    /// <summary>What an argument passed as a message is.</summary>
    public static MessageKind KindOf(IArgumentOperation? argument)
    {
        if (argument is null || argument.ArgumentKind != ArgumentKind.Explicit)
            return MessageKind.None;

        var value = Unwrapped(argument);
        if (value.ConstantValue.HasValue)
            return value.ConstantValue.Value is string ? MessageKind.Constant : value.ConstantValue.Value is null ? MessageKind.None : MessageKind.Value;

        return value.Type?.SpecialType == SpecialType.System_String ? MessageKind.Text : MessageKind.Value;
    }
}
