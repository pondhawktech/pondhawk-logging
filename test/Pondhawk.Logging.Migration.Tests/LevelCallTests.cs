// Copyright (c) Pond Hawk Technologies Inc. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using Pondhawk.Logging.Migration.Tests.Support;
using Xunit;

namespace Pondhawk.Logging.Migration.Tests;

/// <summary>PHLM001: Trace/Debug/Info/Warning/Error become LogTrace/LogDebug/LogInformation/LogWarning/LogError.</summary>
public class LevelCallTests
{
    private static string Code(string body) => $$"""
        using System;
        using Fabrica.Watch;

        class Orders
        {
            void Place(ILogger logger, Exception ex, string text, int count)
            {
                {{body}}
            }
        }
        """;

    [Theory]
    [InlineData("Trace", "LogTrace")]
    [InlineData("Debug", "LogDebug")]
    [InlineData("Info", "LogInformation")]
    [InlineData("Warning", "LogWarning")]
    [InlineData("Error", "LogError")]
    public Task Each_level_becomes_its_Microsoft_Extensions_Logging_method(string fabrica, string logging) =>
        MigrationTest.RunAsync(
            Code($$"""{|PHLM001:logger.{{fabrica}}("Placing the order")|};"""),
            Code($$"""logger.{{logging}}("Placing the order");"""),
            "PHLM001");

    [Fact]
    public Task A_message_computed_at_run_time_becomes_the_value_of_a_Message_hole() =>
        MigrationTest.RunAsync(
            Code("""{|PHLM001:logger.Debug(text)|};"""),
            Code("""logger.LogDebug("{Message}", text);"""),
            "PHLM001");

    [Fact]
    public Task A_message_that_is_not_a_string_becomes_the_value_of_a_Value_hole() =>
        MigrationTest.RunAsync(
            Code("""{|PHLM001:logger.Info(count)|};"""),
            Code("""logger.LogInformation("{Value}", count);"""),
            "PHLM001");

    [Fact]
    public Task An_exception_with_a_message_keeps_both() =>
        MigrationTest.RunAsync(
            Code("""{|PHLM001:logger.Error(ex, "Placing failed")|};"""),
            Code("""logger.LogError(ex, "Placing failed");"""),
            "PHLM001");

    [Fact]
    public Task An_exception_alone_takes_its_own_message() =>
        MigrationTest.RunAsync(
            Code("""{|PHLM001:logger.Error(ex)|};"""),
            Code("""logger.LogError(ex, "{Message}", ex.Message);"""),
            "PHLM001");

    [Fact]
    public Task A_null_message_becomes_an_empty_one() =>
        MigrationTest.RunAsync(
            Code("""{|PHLM001:logger.Warning((object)null)|};"""),
            Code("""logger.LogWarning("");"""),
            "PHLM001");

    // Found in a trial migration: a helper that takes a logger it may not be given.
    [Theory]
    [InlineData("""logger?{|PHLM001:.Warning("Refused")|};""", """logger?.LogWarning("Refused");""", "PHLM001")]
    [InlineData("""logger?{|PHLM002:.Warning("Refused {0}", count)|};""", """logger?.LogWarning("Refused {Count}", count);""", "PHLM002")]
    [InlineData("""logger?{|PHLM003:.Info($"Placed {count}")|};""", """logger?.LogInformation("Placed {Count}", count);""", "PHLM003")]
    [InlineData("""logger?{|PHLM004:.Debug(() => $"Placed {count}")|};""", """if (logger?.IsEnabled(Microsoft.Extensions.Logging.LogLevel.Debug) == true) logger?.LogDebug("Placed {Count}", count);""", "PHLM004")]
    public Task A_null_conditional_call_is_rewritten_in_place(string fabrica, string logging, string rule) =>
        MigrationTest.RunAsync(Code(fabrica), Code(logging), rule);

    [Fact]
    public Task A_null_conditional_call_in_an_expression_is_rewritten_in_place() =>
        MigrationTest.RunAsync(
            Code("""Action place = () => logger?{|PHLM001:.Info("Placed")|};"""),
            Code("""Action place = () => logger?.LogInformation("Placed");"""),
            "PHLM001");

    [Theory]
    [InlineData("""logger.Debug("Placed {0}", count);""")]                // a template with arguments — PHLM002
    [InlineData("""logger.Debug($"Placed {count}");""")]                  // interpolated — PHLM003
    [InlineData("""logger.Debug("Placed " + text);""")]                   // concatenated — PHLM003
    [InlineData("""logger.Debug(() => "Placed");""")]                     // a Func<string> — PHLM004
    [InlineData("""LoggerExtensions.Debug(logger, "Placed");""")]         // called as a static method
    public Task Calls_left_to_other_rules_are_not_reported(string body) =>
        MigrationTest.NoneAsync(Code(body), "PHLM001");

    [Fact]
    public Task A_same_named_method_on_another_logger_is_not_reported() =>
        MigrationTest.NoneAsync("""
            class Mine
            {
                public void Debug(string message) { }
                void Run() => Debug("hello");
            }
            """, "PHLM001");

    [Fact]
    public Task Fix_all_rewrites_every_call_in_the_document() =>
        MigrationTest.RunAsync(
            Code("""
                {|PHLM001:logger.Debug("Placing")|};
                        {|PHLM001:logger.Info(text)|};
                        {|PHLM001:logger.Error(ex, "Failed")|};
                """),
            Code("""
                logger.LogDebug("Placing");
                        logger.LogInformation("{Message}", text);
                        logger.LogError(ex, "Failed");
                """),
            "PHLM001");
}
