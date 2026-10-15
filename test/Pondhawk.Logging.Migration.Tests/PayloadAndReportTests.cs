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

    [Fact]
    public Task An_event_built_by_hand_is_reported() =>
        MigrationTest.ReportAsync(Code("""
            var e = {|PHLM020:logger.CreateEvent(Level.Debug, "Order", state)|};
                    {|PHLM020:logger.LogEvent(e)|};
            """), "PHLM020");

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
}
