// Copyright (c) Pond Hawk Technologies Inc. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using Pondhawk.Logging.Migration.Tests.Support;
using Xunit;

namespace Pondhawk.Logging.Migration.Tests;

/// <summary>
/// PHLM014 and PHLM016 (Fabrica.Watch types written in full) and PHLM015 (the using swap).
/// </summary>
public class TypeAndUsingTests
{
    [Fact]
    public Task A_fully_qualified_logger_type_becomes_Microsoft_Extensions_Logging_ILogger_with_its_using() =>
        MigrationTest.RunAsync("""
            class Orders
            {
                private readonly {|PHLM016:Fabrica.Watch.ILogger|} _logger;
            }
            """, """
            using Microsoft.Extensions.Logging;

            class Orders
            {
                private readonly ILogger _logger;
            }
            """, "PHLM016");

    [Fact]
    public Task A_fully_qualified_logger_type_in_an_alias_is_written_in_full() =>
        MigrationTest.RunAsync("""
            using Log = {|PHLM016:Fabrica.Watch.ILogger|};
            """, """
            using Log = Microsoft.Extensions.Logging.ILogger;
            """, "PHLM016");

    [Fact]
    public Task Fully_qualified_Sensitive_and_PayloadType_become_Pondhawk_Logging_s_with_its_using() =>
        MigrationTest.RunAsync("""
            class Credentials
            {
                [{|PHLM014:Fabrica.Watch.Sensitive|}]
                public string Password { get; set; }

                public object Kind => {|PHLM014:Fabrica.Watch.Sink.PayloadType|}.Json;
            }
            """, """
            using Pondhawk.Logging;

            class Credentials
            {
                [Sensitive]
                public string Password { get; set; }

                public object Kind => PayloadType.Json;
            }
            """, "PHLM014");

    [Fact]
    public Task Simple_names_carry_over_with_the_using_swap_and_are_not_reported() =>
        MigrationTest.NoneAsync("""
            using Fabrica.Watch;
            using Fabrica.Watch.Sink;

            class Credentials
            {
                [Sensitive]
                public string Password { get; set; }

                public PayloadType Kind => PayloadType.Json;

                private ILogger _logger;
            }
            """, "PHLM014", "PHLM016");

    [Fact]
    public Task Once_every_use_carries_over_the_usings_are_swapped() =>
        MigrationTest.RunAsync("""
            using System;
            {|PHLM015:using Fabrica.Watch;|}
            {|PHLM015:using Fabrica.Watch.Sink;|}

            class Orders
            {
                [Sensitive]
                public string Password { get; set; }

                public PayloadType Kind => PayloadType.Sql;

                void Place(ILogger logger, string sql)
                {
                    using var trace = this.EnterMethod();
                    logger.LogSql("Insert", sql);
                    logger.Inspect("sql", sql);
                }
            }
            """, """
            using System;
            using Microsoft.Extensions.Logging;
            using Pondhawk.Logging;

            class Orders
            {
                [Sensitive]
                public string Password { get; set; }

                public PayloadType Kind => PayloadType.Sql;

                void Place(ILogger logger, string sql)
                {
                    using var trace = this.EnterMethod();
                    logger.LogSql("Insert", sql);
                    logger.Inspect("sql", sql);
                }
            }
            """, "PHLM015");

    [Theory]
    [InlineData("""logger.Debug("Placing");""")]                     // PHLM001 still to do
    [InlineData("""var on = logger.IsDebugEnabled;""")]              // PHLM005 still to do
    [InlineData("""logger.LeaveMethod();""")]                        // no rewrite yet
    [InlineData("""logger.Inspect(42, 1);""")]                       // a name that is not a string
    [InlineData("""logger.LogJson("Order", "{}", false);""")]       // the pretty flag
    public Task A_use_that_does_not_carry_over_keeps_the_usings(string body) =>
        MigrationTest.NoneAsync($$"""
            using Fabrica.Watch;

            class Orders
            {
                void Place(ILogger logger)
                {
                    {{body}}
                }
            }
            """, "PHLM015");

    [Fact]
    public Task Only_the_namespaces_the_file_uses_are_added() =>
        MigrationTest.RunAsync("""
            {|PHLM015:using Fabrica.Watch;|}

            class Orders
            {
                private ILogger _logger;
            }
            """, """
            using Microsoft.Extensions.Logging;

            class Orders
            {
                private ILogger _logger;
            }
            """, "PHLM015");

    // Found in fabrica-one: a base class's protected ILogger EnterMethod() is called from other files. Swapping its
    // usings first would change the type under them before their calls are rewritten.
    [Theory]
    [InlineData("protected ILogger EnterMethod() => null;")]
    [InlineData("public ILogger Logger { get; }")]
    [InlineData("internal System.Threading.Tasks.Task<ILogger> Next() => null;")]
    [InlineData("public void Use(ILogger logger) { }")]
    public Task A_file_that_hands_out_Fabrica_Watch_loggers_keeps_its_usings(string member) =>
        MigrationTest.NoneAsync($$"""
            using Fabrica.Watch;

            public abstract class CorrelatedObject
            {
                {{member}}
            }
            """, "PHLM015");

    [Fact]
    public Task A_private_logger_field_does_not_keep_the_usings() =>
        MigrationTest.RunAsync("""
            {|PHLM015:using Fabrica.Watch;|}

            public class Orders
            {
                private ILogger _logger;
            }
            """, """
            using Microsoft.Extensions.Logging;

            public class Orders
            {
                private ILogger _logger;
            }
            """, "PHLM015");
}
