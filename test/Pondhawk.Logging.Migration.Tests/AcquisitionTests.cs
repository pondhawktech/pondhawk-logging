// Copyright (c) Pond Hawk Technologies Inc. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using Pondhawk.Logging.Migration.Tests.Support;
using Xunit;

namespace Pondhawk.Logging.Migration.Tests;

/// <summary>
/// PHLM005 (level checks), PHLM012 (WatchFactoryLocator) and PHLM013 (object extensions that need rewriting).
/// </summary>
public class AcquisitionTests
{
    private static string Code(string body) => $$"""
        using System;
        using Fabrica.Watch;

        class Orders
        {
            object Place(ILogger logger)
            {
                {{body}}
            }
        }
        """;

    [Theory]
    [InlineData("IsTraceEnabled", "Trace")]
    [InlineData("IsDebugEnabled", "Debug")]
    [InlineData("IsInfoEnabled", "Information")]
    [InlineData("IsWarningEnabled", "Warning")]
    [InlineData("IsErrorEnabled", "Error")]
    public Task A_level_check_becomes_IsEnabled(string property, string level) =>
        MigrationTest.RunAsync(
            Code($$"""return {|PHLM005:logger.{{property}}|};"""),
            Code($$"""return logger.IsEnabled(Microsoft.Extensions.Logging.LogLevel.{{level}});"""),
            "PHLM005");

    [Theory]
    [InlineData("""WatchFactoryLocator.Factory.GetLogger("Orders")""", """Pondhawk.Logging.LoggingFactoryLocator.GetFactory().CreateLogger("Orders")""")]
    [InlineData("""WatchFactoryLocator.Factory.GetLogger("Orders", false)""", """Pondhawk.Logging.LoggingFactoryLocator.GetFactory().CreateLogger("Orders")""")]
    [InlineData("""WatchFactoryLocator.Factory.GetLogger<Orders>()""", """Pondhawk.Logging.LoggingFactoryLocator.GetFactory().CreateLogger<Orders>()""")]
    [InlineData("""WatchFactoryLocator.Factory.GetLogger(typeof(Orders))""", """Pondhawk.Logging.LoggingFactoryLocator.GetFactory().CreateLogger(typeof(Orders))""")]
    public Task The_factory_locator_becomes_LoggingFactoryLocator(string fabrica, string pondhawk) =>
        MigrationTest.RunAsync(
            Code($$"""return {|PHLM012:{{fabrica}}|};"""),
            Code($$"""return {{pondhawk}};"""),
            "PHLM012");

    [Fact]
    public Task A_logger_request_has_no_equivalent_and_is_not_reported() =>
        MigrationTest.NoneAsync(Code("""return WatchFactoryLocator.Factory.GetLogger(new LoggerRequest());"""), "PHLM012");

    [Fact]
    public Task EnterMethod_with_type_arguments_loses_them() =>
        MigrationTest.RunAsync(
            Code("""return {|PHLM013:this.EnterMethod<Orders>()|};"""),
            Code("""return this.EnterMethod();"""),
            "PHLM013");

    [Fact]
    public Task EnterMethod_with_a_named_method_name_passes_it_by_position() =>
        MigrationTest.RunAsync(
            Code("""return {|PHLM013:this.EnterMethod(name: "Place")|};"""),
            Code("""return this.EnterMethod("Place");"""),
            "PHLM013");

    [Fact]
    public Task EnterMethodSlim_becomes_a_category_logger_with_EnterMethod() =>
        MigrationTest.RunAsync(
            Code("""return {|PHLM013:this.EnterMethodSlim("Orders.Placing")|};"""),
            Code("""return Pondhawk.Logging.LoggingFactoryLocator.GetFactory().CreateLogger("Orders.Placing").EnterMethod();"""),
            "PHLM013");

    [Theory]
    [InlineData("return this.GetLogger();")]
    [InlineData("return this.EnterMethod();")]
    public Task GetLogger_and_a_plain_EnterMethod_carry_over_and_are_not_reported(string body) =>
        MigrationTest.NoneAsync(Code(body), "PHLM013");
}
