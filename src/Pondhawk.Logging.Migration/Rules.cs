// Copyright (c) Pond Hawk Technologies Inc. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using Microsoft.CodeAnalysis;

namespace Pondhawk.Logging.Migration;

/// <summary>
/// The migration rules. All are Info by default — this is a migration tool, not a gate; an application raises
/// them in <c>.editorconfig</c> to stop new Fabrica.Watch calls appearing mid-migration.
/// </summary>
internal static class Rules
{
    private const string Category = "Migration";
    private const string Guide = "https://github.com/pondhawktech/pondhawk-logging/blob/main/MIGRATING-FROM-FABRICA-WATCH.md";

    public static readonly DiagnosticDescriptor LevelCall = Rule(
        "PHLM001",
        "Fabrica.Watch level call",
        "'{0}' is a Fabrica.Watch call; use '{1}'",
        "Trace, Debug, Info, Warning and Error become LogTrace, LogDebug, LogInformation, LogWarning and LogError. A constant message is kept as the template; any other message becomes the value of a '{Message}' or '{Value}' hole.");

    public static readonly DiagnosticDescriptor LevelEnabled = Rule(
        "PHLM005",
        "Fabrica.Watch level check",
        "'{0}' is a Fabrica.Watch property; use 'IsEnabled(LogLevel.{1})'",
        "IsTraceEnabled, IsDebugEnabled, IsInfoEnabled, IsWarningEnabled and IsErrorEnabled become ILogger.IsEnabled with the matching LogLevel.");

    public static readonly DiagnosticDescriptor FactoryLocator = Rule(
        "PHLM012",
        "Fabrica.Watch logger acquisition",
        "'{0}' acquires a Fabrica.Watch logger; use 'LoggingFactoryLocator.GetFactory().CreateLogger'",
        "WatchFactoryLocator.Factory.GetLogger(category), GetLogger<T>() and GetLogger(Type) become LoggingFactoryLocator.GetFactory().CreateLogger with the same category.");

    public static readonly DiagnosticDescriptor ObjectExtension = Rule(
        "PHLM013",
        "Fabrica.Watch object extension",
        "'{0}' needs rewriting for Pondhawk.Logging's object extensions",
        "this.GetLogger() and this.EnterMethod() carry over with the using swap (PHLM015). EnterMethod with explicit type arguments or a named argument, and EnterMethodSlim(category), are rewritten.");

    public static readonly DiagnosticDescriptor SharedType = Rule(
        "PHLM014",
        "Fabrica.Watch type with a Pondhawk.Logging equivalent",
        "'{0}' names Fabrica.Watch's type; use Pondhawk.Logging's",
        "A fully qualified Fabrica.Watch.SensitiveAttribute or Fabrica.Watch.Sink.PayloadType is rewritten to Pondhawk.Logging's. A simple name carries over with the using swap (PHLM015). PayloadType's members keep their names but not their numbers: convert by name, never through an int.");

    public static readonly DiagnosticDescriptor Usings = Rule(
        "PHLM015",
        "Fabrica.Watch using no longer needed",
        "Every Fabrica.Watch use left in this file carries over to Microsoft.Extensions.Logging and Pondhawk.Logging; replace 'using {0};'",
        "Reported once nothing in the file needs Fabrica.Watch except names that mean the same under Microsoft.Extensions.Logging and Pondhawk.Logging (ILogger, [Sensitive], PayloadType, GetLogger, EnterMethod, LogSql, LogObject …). The fix replaces the Fabrica.Watch usings with those two.");

    public static readonly DiagnosticDescriptor LoggerType = Rule(
        "PHLM016",
        "Fabrica.Watch logger type",
        "'{0}' names Fabrica.Watch's ILogger; use Microsoft.Extensions.Logging's",
        "A fully qualified Fabrica.Watch.ILogger becomes Microsoft.Extensions.Logging.ILogger. A simple 'ILogger' carries over with the using swap (PHLM015).");

    public static readonly DiagnosticDescriptor Template = Rule(
        "PHLM002",
        "Fabrica.Watch positional template",
        "'{0}' formats a positional template; use '{1}' with named holes",
        "A string.Format template ({0}, {1:N2}) becomes a message template whose holes are named from the arguments (order.Id → {OrderId}), keeping alignment and format. A template that cannot be read is formatted as before and marked TODO.");

    public static readonly DiagnosticDescriptor Parts = Rule(
        "PHLM003",
        "Fabrica.Watch message built at the call",
        "'{0}' builds its message at the call; use '{1}' with a message template",
        "An interpolated or concatenated message becomes a message template and its values, so it is not formatted when the level is off.");

    public static readonly DiagnosticDescriptor Lazy = Rule(
        "PHLM004",
        "Fabrica.Watch lazy message",
        "'{0}' takes a Func<string>; use '{1}' behind an IsEnabled check",
        "logger.Debug(() => …) becomes if (logger.IsEnabled(LogLevel.Debug)) logger.LogDebug(…), so the message is still built only when the level is on.");

    public static readonly DiagnosticDescriptor MethodTrace = Rule(
        "PHLM006",
        "Fabrica.Watch method tracing",
        "'{0}' is Fabrica.Watch method tracing; Pondhawk.Logging ends a method's trace when its EnterMethod is disposed",
        "LeaveMethod goes and the EnterMethod becomes a using declaration; a try/finally that only closed the trace goes with it.");

    public static readonly DiagnosticDescriptor Scope = Rule(
        "PHLM007",
        "Fabrica.Watch scope",
        "'{0}' is a Fabrica.Watch scope; use 'using (logger.EnterMethod(name)) {{ … }}'",
        "EnterScope/LeaveScope in one block become a using statement around what they bracketed.");

    public static readonly DiagnosticDescriptor ContextCall = Rule(
        "PHLM008",
        "Fabrica.Watch error or warning with context",
        "'{0}' needs rewriting for Pondhawk.Logging",
        "ErrorWithContext needs a string message in Pondhawk.Logging. WarningWithContext becomes LogObject(LogLevel.Warning, \"Context\", context) and LogWarning(exception, message).");

    public static readonly DiagnosticDescriptor InspectName = Rule(
        "PHLM009",
        "Fabrica.Watch Inspect with a name that is not a string",
        "'Inspect' takes a string name in Pondhawk.Logging",
        "A constant name becomes its text; any other becomes an interpolated string.");

    public static readonly DiagnosticDescriptor ObjectLevel = Rule(
        "PHLM010",
        "LogObject moves from Debug to Trace",
        "'LogObject' logs at Trace in Pondhawk.Logging, at Debug in Fabrica.Watch",
        "No rewrite: LogObject without a level always logs at Trace, since its payload is expensive to build. A category switched to Debug no longer shows these objects.");

    public static readonly DiagnosticDescriptor Pretty = Rule(
        "PHLM011",
        "Fabrica.Watch pretty flag",
        "'{0}' has no pretty flag in Pondhawk.Logging; the content is logged as given",
        "The pretty argument of LogJson and LogXml is dropped.");

    public static readonly DiagnosticDescriptor Events = Rule(
        "PHLM020",
        "Fabrica.Watch event built by hand",
        "'{0}' builds a Fabrica.Watch event by hand; use a levelled payload method or ErrorWithContext",
        "No rewrite. LogJson(level, …), LogText(level, …), LogObject(level, …) and ErrorWithContext carry a payload on the event that needs it; see the migration guide.");

    public static readonly DiagnosticDescriptor Bootstrap = Rule(
        "PHLM021",
        "Fabrica.Watch set-up",
        "'{0}' sets Fabrica.Watch up; configure logging with AddWatch, AddCloudWatch or AddJournaldConsole",
        "No rewrite. Replace WatchFactoryBuilder with the providers on ILoggingBuilder, and call LoggingFactoryLocator.SetFactory once, after the host is built; see the migration guide.");

    public static readonly DiagnosticDescriptor Bridge = Rule(
        "PHLM022",
        "Fabrica.Watch bridge",
        "'{0}' bridges Microsoft.Extensions.Logging to Fabrica.Watch; with Pondhawk.Logging it is not needed",
        "No rewrite. Remove the bridge provider: Microsoft.Extensions.Logging is the API now.");

    public static readonly DiagnosticDescriptor ScopeState = Rule(
        "PHLM023",
        "Fabrica.Watch logger state",
        "'{0}' has no Pondhawk.Logging equivalent",
        "No rewrite. GetCurrentScope, SetCurrentScope and a logger's Category have no equivalent on ILogger; see the migration guide.");

    private static DiagnosticDescriptor Rule(string id, string title, string message, string description) =>
        new(id, title, message, Category, DiagnosticSeverity.Info, isEnabledByDefault: true, description, Guide + "#" + id.ToLowerInvariant());
}
