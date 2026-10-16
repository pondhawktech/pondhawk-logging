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
    [InlineData("""logger.WarningWithContext(ex, state)""", """logger.WarningWithContext(ex, state, ex.Message)""")]
    [InlineData("""logger.WarningWithContext(ex, state, count)""", """logger.WarningWithContext(ex, state, $"{count}")""")]
    public Task Error_and_WarningWithContext_get_a_string_message(string fabrica, string logging) =>
        MigrationTest.RunAsync(Code("{|PHLM008:" + fabrica + "|};"), Code(logging + ";"), "PHLM008");

    // The exception-less overloads are found in fabrica-one: its copy of Fabrica.Watch adds them.
    [Theory]
    [InlineData("""logger.ErrorWithContext(ex, state, "Placing failed");""")]
    [InlineData("""logger.WarningWithContext(ex, state, "Retrying");""")]
    [InlineData("""logger.ErrorWithContext(state, "Placing failed");""")]
    [InlineData("""logger.WarningWithContext(state, "Retrying");""")]
    [InlineData("""Action log = () => logger.WarningWithContext(state, "Retrying");""")]
    public Task An_Error_or_WarningWithContext_with_a_string_message_carries_over(string body) =>
        MigrationTest.NoneAsync(Code(body), "PHLM008");

    [Theory]
    [InlineData("""logger.InfoWithContext(state, "Placing")""", """logger.LogWithContext(Microsoft.Extensions.Logging.LogLevel.Information, state, "Placing")""")]
    [InlineData("""Action log = () => logger.InfoWithContext(state, "Placing")""", """Action log = () => logger.LogWithContext(Microsoft.Extensions.Logging.LogLevel.Information, state, "Placing")""")]
    public Task InfoWithContext_becomes_one_LogWithContext_call(string fabrica, string logging) =>
        MigrationTest.RunAsync(
            Code(fabrica.Replace("logger.InfoWithContext", "{|PHLM008:logger.InfoWithContext", StringComparison.Ordinal) + "|};"),
            Code(logging + ";"),
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
    [InlineData("""logger.CreateEvent(Level.Warning, "Retrying", ex, state)""", """logger.WarningWithContext(ex, state, "Retrying");""")]
    [InlineData("""logger.CreateEvent(Level.Debug, count, state)""", """logger.LogObject(Microsoft.Extensions.Logging.LogLevel.Debug, $"{count}", state);""")]
    public Task An_event_created_and_logged_at_once_becomes_one_call(string created, string logging) =>
        MigrationTest.RunAsync(
            Code("var e = {|PHLM020:" + created + "|};\n        {|PHLM020:logger.LogEvent(e)|};"),
            Code(logging),
            "PHLM020");

    [Fact]
    public Task An_event_with_context_below_warning_becomes_LogWithContext() =>
        MigrationTest.RunAsync(
            Code("""
                var e = {|PHLM020:logger.CreateEvent(Level.Info, "Retrying", ex, state)|};
                        {|PHLM020:logger.LogEvent(e)|};
                """),
            Code("""
                logger.LogWithContext(Microsoft.Extensions.Logging.LogLevel.Information, ex, state, "Retrying");
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
