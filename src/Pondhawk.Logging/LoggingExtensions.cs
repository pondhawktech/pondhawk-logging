// Copyright (c) Pond Hawk Technologies Inc. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System.Globalization;
using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using Pondhawk.Logging.Serializers;
using Pondhawk.Logging.Utilities;

namespace Pondhawk.Logging;

/// <summary>
/// Extension methods on <see cref="ILogger"/> for method tracing, object serialization, and typed
/// payloads. They attach a <see cref="LogState"/> carrying well-known control properties (see
/// <see cref="LogPropertyNames"/>) that a sink such as the Watch provider reads back. Every method guards
/// on <see cref="ILogger.IsEnabled"/> first, so a switch-dropped category pays no serialization cost.
/// </summary>
public static class LoggingExtensions
{
    /// <summary>
    /// Creates a disposable method-tracing scope: logs entry at <see cref="LogLevel.Trace"/> and logs exit
    /// with elapsed time on dispose. The returned <see cref="MethodLogger"/> is itself an
    /// <see cref="ILogger"/> and can be used as the logger for the method body.
    /// </summary>
    /// <param name="logger">The logger to trace with.</param>
    /// <param name="method">The calling method name (auto-populated by the compiler).</param>
    /// <returns>A disposable <see cref="MethodLogger"/> that also implements <see cref="ILogger"/>.</returns>
    public static MethodLogger EnterMethod(this ILogger logger, [CallerMemberName] string method = "")
    {
        var tracing = logger.IsEnabled(LogLevel.Trace);

        if (tracing)
        {
            var properties = new KeyValuePair<string, object?>[] { new(LogPropertyNames.Nesting, 1) };
            var state = new LogState("Entering " + method, properties);
            logger.Log(LogLevel.Trace, default, state, null, LogState.Formatter);
        }

        return new MethodLogger(logger, method, tracing);
    }

    /// <summary>Serializes an object to JSON and logs it as a payload titled with the type name.</summary>
    /// <typeparam name="T">The type of the object to serialize.</typeparam>
    /// <param name="logger">The logger.</param>
    /// <param name="value">The object to serialize.</param>
    public static void LogObject<T>(this ILogger logger, T value)
    {
        if (!logger.IsEnabled(LogLevel.Trace))
            return;

        var (_, json) = JsonObjectSerializer.Instance.Serialize(value);
        var title = typeof(T).GetConciseName();
        EmitPayload(logger, LogLevel.Trace, title, PayloadType.Json, json);
    }

    /// <summary>Serializes an object to JSON and logs it as a payload with a custom title.</summary>
    /// <typeparam name="T">The type of the object to serialize.</typeparam>
    /// <param name="logger">The logger.</param>
    /// <param name="title">The log message title.</param>
    /// <param name="value">The object to serialize.</param>
    public static void LogObject<T>(this ILogger logger, string title, T value)
    {
        if (!logger.IsEnabled(LogLevel.Trace))
            return;

        var (_, json) = JsonObjectSerializer.Instance.Serialize(value);
        EmitPayload(logger, LogLevel.Trace, title, PayloadType.Json, json);
    }

    /// <summary>Logs a JSON string as a <see cref="PayloadType.Json"/> payload.</summary>
    /// <param name="logger">The logger.</param>
    /// <param name="title">The log message title.</param>
    /// <param name="json">The JSON content to attach.</param>
    public static void LogJson(this ILogger logger, string title, string? json)
        => LogPayload(logger, title, json, PayloadType.Json);

    /// <summary>Logs a SQL string as a <see cref="PayloadType.Sql"/> payload.</summary>
    /// <param name="logger">The logger.</param>
    /// <param name="title">The log message title.</param>
    /// <param name="sql">The SQL content to attach.</param>
    public static void LogSql(this ILogger logger, string title, string? sql)
        => LogPayload(logger, title, sql, PayloadType.Sql);

    /// <summary>Logs an XML string as a <see cref="PayloadType.Xml"/> payload.</summary>
    /// <param name="logger">The logger.</param>
    /// <param name="title">The log message title.</param>
    /// <param name="xml">The XML content to attach.</param>
    public static void LogXml(this ILogger logger, string title, string? xml)
        => LogPayload(logger, title, xml, PayloadType.Xml);

    /// <summary>Logs a YAML string as a <see cref="PayloadType.Yaml"/> payload.</summary>
    /// <param name="logger">The logger.</param>
    /// <param name="title">The log message title.</param>
    /// <param name="yaml">The YAML content to attach.</param>
    public static void LogYaml(this ILogger logger, string title, string? yaml)
        => LogPayload(logger, title, yaml, PayloadType.Yaml);

    /// <summary>Logs a plain text string as a <see cref="PayloadType.Text"/> payload.</summary>
    /// <param name="logger">The logger.</param>
    /// <param name="title">The log message title.</param>
    /// <param name="text">The text content to attach.</param>
    public static void LogText(this ILogger logger, string title, string? text)
        => LogPayload(logger, title, text, PayloadType.Text);

    /// <summary>Logs a name/value pair as <c>"{Name} = {Value}"</c> at <see cref="LogLevel.Debug"/>.</summary>
    /// <param name="logger">The logger.</param>
    /// <param name="name">The display name for the value.</param>
    /// <param name="value">The value to log.</param>
    public static void Inspect(this ILogger logger, string name, object? value)
    {
        if (!logger.IsEnabled(LogLevel.Debug))
            return;

        var text = value is null ? "null" : Convert.ToString(value, CultureInfo.InvariantCulture) ?? string.Empty;
        var title = name + " = " + text;
        var properties = new KeyValuePair<string, object?>[]
        {
            new("Name", name),
            new("Value", value),
        };

        var state = new LogState(title, properties);
        logger.Log(LogLevel.Debug, default, state, null, LogState.Formatter);
    }

    private static void LogPayload(ILogger logger, string title, string? content, PayloadType payloadType)
    {
        if (!logger.IsEnabled(LogLevel.Trace))
            return;

        EmitPayload(logger, LogLevel.Trace, title, payloadType, content ?? string.Empty);
    }

    private static void EmitPayload(ILogger logger, LogLevel level, string title, PayloadType payloadType, string content)
    {
        var properties = new KeyValuePair<string, object?>[]
        {
            new(LogPropertyNames.PayloadType, (int)payloadType),
            new(LogPropertyNames.PayloadContent, content),
        };

        var state = new LogState(title, properties);
        logger.Log(level, default, state, null, LogState.Formatter);
    }
}
