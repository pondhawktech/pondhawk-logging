// Copyright (c) Pond Hawk Technologies Inc. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System;
using System.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.Operations;

namespace Pondhawk.Logging.Migration;

/// <summary>
/// A Fabrica.Watch <c>…WithContext</c> call (PHLM008): <c>ErrorWithContext</c> and <c>WarningWithContext</c>, with an
/// exception or — in copies of Fabrica.Watch that added them — without one, and any other level's (an
/// <c>InfoWithContext</c>). Its arguments are found by parameter, not position, since those copies differ.
/// </summary>
internal sealed class ContextCall
{
    private ContextCall(string level, IArgumentOperation? exception, IArgumentOperation? context, IArgumentOperation? message)
    {
        Level = level;
        Exception = exception;
        Context = context;
        Message = message;
    }

    /// <summary>The <c>LogLevel</c> member it logs at.</summary>
    public string Level { get; }

    /// <summary>The exception, when the overload takes one.</summary>
    public IArgumentOperation? Exception { get; }

    /// <summary>The context object.</summary>
    public IArgumentOperation? Context { get; }

    /// <summary>The message, when one was written.</summary>
    public IArgumentOperation? Message { get; }

    /// <summary>
    /// <c>ErrorWithContext</c> or <c>WarningWithContext</c> with a string message, with an exception or without
    /// one — Pondhawk.Logging has exactly those, so it carries over with the using swap.
    /// </summary>
    public bool CarriesOver =>
        HasOwnMethod
        && Message is { ArgumentKind: ArgumentKind.Explicit } message
        && LevelCall.Unwrapped(message).Type?.SpecialType == SpecialType.System_String;

    /// <summary>Error and Warning have a <c>…WithContext</c> of their own in Pondhawk.Logging; the other levels use <c>LogWithContext(level, …)</c>.</summary>
    public bool HasOwnMethod =>
        string.Equals(Level, "Error", StringComparison.Ordinal) || string.Equals(Level, "Warning", StringComparison.Ordinal);

    /// <summary>The Pondhawk.Logging method it becomes.</summary>
    public string Equivalent => HasOwnMethod ? Level + "WithContext" : "LogWithContext";

    /// <summary>The <c>…WithContext</c> call this invocation is, or null.</summary>
    public static ContextCall? Match(IInvocationOperation invocation)
    {
        var declared = FabricaWatch.Declared(invocation);
        if (!declared.Name.EndsWith("WithContext", StringComparison.Ordinal)
            || !FabricaWatch.Calls(invocation, FabricaWatch.LoggerExtensions, declared.Name))
            return null;

        var level = declared.Name.Substring(0, declared.Name.Length - "WithContext".Length) switch
        {
            "Trace" => "Trace",
            "Debug" => "Debug",
            "Info" => "Information",
            "Warning" => "Warning",
            "Error" => "Error",
            _ => null,
        };

        if (level is null)
            return null;

        IArgumentOperation? Find(Func<IParameterSymbol, bool> match) =>
            invocation.Arguments.FirstOrDefault(a => a.Parameter is { Ordinal: > 0 } p && match(p));

        return new ContextCall(
            level,
            Find(p => string.Equals(p.Type.ToDisplayString(), "System.Exception", StringComparison.Ordinal)),
            Find(p => string.Equals(p.Name, "context", StringComparison.Ordinal)),
            Find(p => string.Equals(p.Name, "message", StringComparison.Ordinal)));
    }
}
