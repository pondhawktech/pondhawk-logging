// Copyright (c) Pond Hawk Technologies Inc. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System.Runtime.CompilerServices;
using Pondhawk.Logging.Serializers;
using Pondhawk.Logging.Utilities;
using Serilog;
using Serilog.Events;
#pragma warning disable CA2254

namespace Pondhawk.Logging;

/// <summary>
/// Extension methods on <see cref="Serilog.ILogger"/> for method tracing, object serialization, and typed payloads.
/// </summary>
public static class SerilogExtensions
{
    /// <summary>
    /// Creates a disposable method tracing scope that logs entry at Verbose level
    /// and logs exit with elapsed time on dispose. The returned <see cref="MethodLogger"/>
    /// implements <see cref="ILogger"/> and can be used as the logger for the method body.
    /// </summary>
    /// <param name="logger">The Serilog logger to trace with.</param>
    /// <param name="method">The calling method name (auto-populated by compiler).</param>
    /// <returns>A disposable <see cref="MethodLogger"/> that also implements <see cref="ILogger"/>.</returns>
    public static MethodLogger EnterMethod(
        this ILogger logger,
        [CallerMemberName] string method = "")
    {
        var tracing = logger.IsEnabled(LogEventLevel.Verbose);

        if (tracing)
        {
            logger
                .ForContext(LogPropertyNames.Nesting, 1)
                .Verbose("Entering {Method}", method);
        }

        return new MethodLogger(logger, method, tracing);
    }

    /// <summary>
    /// Serializes an object to JSON and logs it as a structured payload with the type name as the message.
    /// </summary>
    /// <typeparam name="T">The type of the object to serialize.</typeparam>
    /// <param name="logger">The Serilog logger.</param>
    /// <param name="value">The object to serialize.</param>
    public static void LogObject<T>(
        this ILogger logger,
        T value
        )
    {
        if (!logger.IsEnabled(LogEventLevel.Verbose))
            return;

        var (_, json) = JsonObjectSerializer.Instance.Serialize(value);
        var typeName = typeof(T).GetConciseName();

        logger
            .ForContext(LogPropertyNames.PayloadType, (int)PayloadType.Json)
            .ForContext(LogPropertyNames.PayloadContent, json)
            .Write(LogEventLevel.Verbose, typeName);
    }

    /// <summary>
    /// Serializes an object to JSON and logs it as a structured payload with a custom title.
    /// </summary>
    /// <typeparam name="T">The type of the object to serialize.</typeparam>
    /// <param name="logger">The Serilog logger.</param>
    /// <param name="title">The log message title.</param>
    /// <param name="value">The object to serialize.</param>
    public static void LogObject<T>(
        this ILogger logger,
        string title,
        T value)
    {
        if (!logger.IsEnabled(LogEventLevel.Verbose))
            return;

        var (_, json) = JsonObjectSerializer.Instance.Serialize(value);

        logger
            .ForContext(LogPropertyNames.PayloadType, (int)PayloadType.Json)
            .ForContext(LogPropertyNames.PayloadContent, json)
            .Write(LogEventLevel.Verbose, title);
    }

    /// <summary>
    /// Logs a JSON string as a payload with <see cref="PayloadType.Json"/> syntax highlighting.
    /// </summary>
    /// <param name="logger">The Serilog logger.</param>
    /// <param name="title">The log message title.</param>
    /// <param name="json">The JSON content to attach.</param>
    public static void LogJson(
        this ILogger logger,
        string title,
        string? json)
    {
        LogPayload(logger, title, json, PayloadType.Json, LogEventLevel.Verbose);
    }

    /// <summary>
    /// Logs a SQL string as a payload with <see cref="PayloadType.Sql"/> syntax highlighting.
    /// </summary>
    /// <param name="logger">The Serilog logger.</param>
    /// <param name="title">The log message title.</param>
    /// <param name="sql">The SQL content to attach.</param>
    public static void LogSql(
        this ILogger logger,
        string title,
        string? sql)
    {
        LogPayload(logger, title, sql, PayloadType.Sql, LogEventLevel.Verbose);
    }

    /// <summary>
    /// Logs an XML string as a payload with <see cref="PayloadType.Xml"/> syntax highlighting.
    /// </summary>
    /// <param name="logger">The Serilog logger.</param>
    /// <param name="title">The log message title.</param>
    /// <param name="xml">The XML content to attach.</param>
    public static void LogXml(
        this ILogger logger,
        string title,
        string? xml)
    {
        LogPayload(logger, title, xml, PayloadType.Xml, LogEventLevel.Verbose);
    }

    /// <summary>
    /// Logs a YAML string as a payload with <see cref="PayloadType.Yaml"/> syntax highlighting.
    /// </summary>
    /// <param name="logger">The Serilog logger.</param>
    /// <param name="title">The log message title.</param>
    /// <param name="yaml">The YAML content to attach.</param>
    public static void LogYaml(
        this ILogger logger,
        string title,
        string? yaml)
    {
        LogPayload(logger, title, yaml, PayloadType.Yaml, LogEventLevel.Verbose);
    }

    /// <summary>
    /// Logs a plain text string as a payload with <see cref="PayloadType.Text"/> type.
    /// </summary>
    /// <param name="logger">The Serilog logger.</param>
    /// <param name="title">The log message title.</param>
    /// <param name="text">The text content to attach.</param>
    public static void LogText(
        this ILogger logger,
        string title,
        string? text)
    {
        LogPayload(logger, title, text, PayloadType.Text, LogEventLevel.Verbose);
    }

    /// <summary>
    /// Logs a name/value pair as <c>"{Name} = {Value}"</c> at the specified level.
    /// </summary>
    /// <param name="logger">The Serilog logger.</param>
    /// <param name="name">The display name for the value.</param>
    /// <param name="value">The value to log.</param>
    public static void Inspect(
        this ILogger logger,
        string name,
        object? value)
    {
        logger.Write(LogEventLevel.Debug, "{Name} = {Value}", name, value);
    }

    private static void LogPayload(
        ILogger logger,
        string title,
        string? content,
        PayloadType payloadType,
        LogEventLevel level)
    {
        if (!logger.IsEnabled(level))
            return;

        logger
            .ForContext(LogPropertyNames.PayloadType, (int)payloadType)
            .ForContext(LogPropertyNames.PayloadContent, content ?? string.Empty)
            .Write(level, title);
    }
}
