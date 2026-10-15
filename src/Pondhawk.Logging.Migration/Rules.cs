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

    private static DiagnosticDescriptor Rule(string id, string title, string message, string description) =>
        new(id, title, message, Category, DiagnosticSeverity.Info, isEnabledByDefault: true, description, Guide + "#" + id.ToLowerInvariant());
}
