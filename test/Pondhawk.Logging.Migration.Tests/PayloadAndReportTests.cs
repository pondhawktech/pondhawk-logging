// Copyright (c) Pond Hawk Technologies Inc. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using Pondhawk.Logging.Migration.Tests.Support;
using Xunit;

namespace Pondhawk.Logging.Migration.Tests;

/// <summary>PHLM008–PHLM011 (context, Inspect, LogObject, pretty) and the report-only PHLM020–PHLM023.</summary>
public class PayloadAndReportTests
{
    private static string Code(string body) => $$"""
        using System;
        using Fabrica.Watch;

        class Orders
        {
            void Place(ILogger logger, Exception ex, object state, string json, int count)
            {
                {{body}}
            }
        }
        """;

    [Theory]
    [InlineData("""logger.ErrorWithContext(ex, state)""", """logger.ErrorWithContext(ex, state, ex.Message)""")]
    [InlineData("""logger.ErrorWithContext(ex, state, count)""", """logger.ErrorWithContext(ex, state, $"{count}")""")]
    public Task ErrorWithContext_gets_a_string_message(string fabrica, string logging) =>
        MigrationTest.RunAsync(Code("{|PHLM008:" + fabrica + "|};"), Code(logging + ";"), "PHLM008");

    [Fact]
    public Task An_ErrorWithContext_with_a_string_message_carries_over() =>
        MigrationTest.NoneAsync(Code("""logger.ErrorWithContext(ex, state, "Placing failed");"""), "PHLM008");

    [Fact]
    public Task WarningWithContext_logs_the_context_then_the_warning() =>
        MigrationTest.RunAsync(
            Code("""{|PHLM008:logger.WarningWithContext(ex, state, "Retrying")|};"""),
            Code("""
                {
                            logger.LogObject(Microsoft.Extensions.Logging.LogLevel.Warning, "Context", state);
                            logger.LogWarning(ex, "Retrying");
                        }
                """),
            "PHLM008");

    // Found in fabrica-one: its copy of Fabrica.Watch adds …WithContext overloads without an exception.
    [Theory]
    [InlineData("ErrorWithContext", "Error", "LogError")]
    [InlineData("WarningWithContext", "Warning", "LogWarning")]
    [InlineData("InfoWithContext", "Information", "LogInformation")]
    public Task A_context_call_without_an_exception_logs_the_context_then_the_message(string fabrica, string level, string logging) =>
        MigrationTest.RunAsync(
            Code($$"""{|PHLM008:logger.{{fabrica}}(state, "Placing")|};"""),
            Code($$"""
                {
                            logger.LogObject(Microsoft.Extensions.Logging.LogLevel.{{level}}, "Context", state);
                            logger.{{logging}}("Placing");
                        }
                """),
            "PHLM008");

    [Fact]
    public Task A_context_call_in_an_expression_logs_the_message_and_marks_the_lost_context() =>
        MigrationTest.RunAsync(
            Code("""Action log = () => {|PHLM008:logger.WarningWithContext(state, "Retrying")|};"""),
            Code("""Action log = () => logger.LogWarning("Retrying") /* TODO(Fabrica.Watch migration): the context object is no longer logged; log it with LogObject(LogLevel.Warning, "Context", state) */;"""),
            "PHLM008");

    [Theory]
    [InlineData("""logger.Inspect(42, count)""", """logger.Inspect("42", count)""")]
    [InlineData("""logger.Inspect(state, count)""", """logger.Inspect($"{state}", count)""")]
    public Task Inspect_gets_a_string_name(string fabrica, string logging) =>
        MigrationTest.RunAsync(Code("{|PHLM009:" + fabrica + "|};"), Code(logging + ";"), "PHLM009");

    [Theory]
    [InlineData("""logger.LogJson("Order", json, false)""", """logger.LogJson("Order", json)""")]
    [InlineData("""logger.LogXml("Order", json, true)""", """logger.LogXml("Order", json)""")]
    public Task The_pretty_flag_is_dropped(string fabrica, string logging) =>
        MigrationTest.RunAsync(Code("{|PHLM011:" + fabrica + "|};"), Code(logging + ";"), "PHLM011");

    [Fact]
    public Task LogObject_is_reported_for_its_level_and_left_alone() =>
        MigrationTest.ReportAsync(Code("""{|PHLM010:logger.LogObject("Order", state)|};"""), "PHLM010");

    // Found in fabrica-one: every event it builds by hand is created and logged at once.
    [Theory]
    [InlineData("""logger.CreateEvent(Level.Info, "Appliance Info", Fabrica.Watch.Sink.PayloadType.Yaml, json)""", """logger.LogYaml(Microsoft.Extensions.Logging.LogLevel.Information, "Appliance Info", json);""")]
    [InlineData("""logger.CreateEvent(Level.Error, "Malformed JSON", Fabrica.Watch.Sink.PayloadType.Text, json)""", """logger.LogText(Microsoft.Extensions.Logging.LogLevel.Error, "Malformed JSON", json);""")]
    [InlineData("""logger.CreateEvent(Level.Error, "Plan schema", Fabrica.Watch.Sink.PayloadType.Json, json)""", """logger.LogJson(Microsoft.Extensions.Logging.LogLevel.Error, "Plan schema", json);""")]
    [InlineData("""logger.CreateEvent(Level.Error, "Start failed.", state)""", """logger.LogObject(Microsoft.Extensions.Logging.LogLevel.Error, "Start failed.", state);""")]
    [InlineData("""logger.CreateEvent(Level.Warning, "Retrying")""", """logger.LogWarning("Retrying");""")]
    [InlineData("""logger.CreateEvent(Level.Error, "Failed", ex, state)""", """logger.ErrorWithContext(ex, state, "Failed");""")]
    [InlineData("""logger.CreateEvent(Level.Debug, count, state)""", """logger.LogObject(Microsoft.Extensions.Logging.LogLevel.Debug, $"{count}", state);""")]
    public Task An_event_created_and_logged_at_once_becomes_one_call(string created, string logging) =>
        MigrationTest.RunAsync(
            Code("var e = {|PHLM020:" + created + "|};\n        {|PHLM020:logger.LogEvent(e)|};"),
            Code(logging),
            "PHLM020");

    [Fact]
    public Task An_event_with_context_below_error_logs_the_context_then_the_message() =>
        MigrationTest.RunAsync(
            Code("""
                var e = {|PHLM020:logger.CreateEvent(Level.Warning, "Retrying", ex, state)|};
                        {|PHLM020:logger.LogEvent(e)|};
                """),
            Code("""
                logger.LogObject(Microsoft.Extensions.Logging.LogLevel.Warning, "Context", state);
                        logger.LogWarning(ex, "Retrying");
                """),
            "PHLM020");

    [Theory]
    [InlineData("""
        var e = {|PHLM020:logger.CreateEvent(Level.Debug, "Order", state)|};
                Console.WriteLine(e);
                {|PHLM020:logger.LogEvent(e)|};
        """)]                                                                        // used for something else too
    [InlineData("""
        var level = {|PHLM024:Level.Debug|};
                var e = {|PHLM020:logger.CreateEvent(level, "Order", state)|};
                {|PHLM020:logger.LogEvent(e)|};
        """)]                                                                        // the level is not a constant (and Level itself is reported)
    public Task An_event_that_is_not_simply_created_and_logged_stays_reported(string body) =>
        MigrationTest.ReportAsync(Code(body), "PHLM020", "PHLM024");

    [Fact]
    public Task Fabrica_Watch_set_up_is_reported() =>
        MigrationTest.ReportAsync(Code("""
            {|PHLM021:{|PHLM021:WatchFactoryBuilder.Create()|}.UseLocalSwitchSource()|};
                    {|PHLM021:WatchFactoryLocator.SetFactory(null)|};
                    var b = {|PHLM021:new WatchFactoryBuilder()|};
            """), "PHLM021");

    [Fact]
    public Task The_bridge_is_reported() =>
        MigrationTest.ReportAsync(Code("""
            var provider = {|PHLM022:new Fabrica.Watch.Bridges.MicrosoftImpl.LoggerProvider()|};
            """), "PHLM022");

    [Fact]
    public Task A_loggers_own_state_is_reported() =>
        MigrationTest.ReportAsync(Code("""
            var scope = {|PHLM023:logger.GetCurrentScope()|};
                    {|PHLM023:logger.SetCurrentScope(scope)|};
                    var category = {|PHLM023:logger.Category|};
            """), "PHLM023");

    // Found in fabrica-one: WatchCorrelation.Begin, an API its copy of Fabrica.Watch adds. Nothing rewrites it, so it
    // is reported, and the file keeps its usings until it is migrated by hand.
    [Fact]
    public Task A_Fabrica_Watch_use_no_rule_knows_is_reported_and_keeps_the_usings() =>
        MigrationTest.ReportAsync("""
            using Fabrica.Watch;

            class Orders
            {
                void Place()
                {
                    using var scope = {|PHLM024:WatchCorrelation.Begin("7")|};
                }
            }
            """, "PHLM024", "PHLM015");
}
