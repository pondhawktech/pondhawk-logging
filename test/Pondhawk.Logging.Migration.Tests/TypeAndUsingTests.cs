// Copyright (c) Pond Hawk Technologies Inc. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using Pondhawk.Logging.Migration.Tests.Support;
using Xunit;

namespace Pondhawk.Logging.Migration.Tests;

/// <summary>
/// PHLM014 and PHLM016 (Fabrica.Watch types written in full), PHLM015 (the using swap) and PHLM017 (loggers
/// handed to other files).
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

    // PHLM017

    [Theory]
    [InlineData("protected {|PHLM017:ILogger|} EnterMethod() => null;", "protected Pondhawk.Logging.MethodLogger EnterMethod() => null;")]
    [InlineData("protected {|PHLM017:ILogger|} Trace() => this.EnterMethod();", "protected Pondhawk.Logging.MethodLogger Trace() => this.EnterMethod();")]
    [InlineData("public {|PHLM017:ILogger|} Logger { get; }", "public Microsoft.Extensions.Logging.ILogger Logger { get; }")]
    [InlineData("internal System.Threading.Tasks.Task<{|PHLM017:ILogger|}> Next() => null;", "internal System.Threading.Tasks.Task<Microsoft.Extensions.Logging.ILogger> Next() => null;")]
    [InlineData("public void Use({|PHLM017:ILogger|}? logger) { }", "public void Use(Microsoft.Extensions.Logging.ILogger? logger) { }")]
    public Task A_member_that_hands_out_a_Fabrica_Watch_logger_is_retyped(string member, string retyped) =>
        MigrationTest.RunAsync($$"""
            #nullable enable
            using Fabrica.Watch;

            public abstract class CorrelatedObject
            {
                {{member}}
            }
            """, $$"""
            #nullable enable
            using Fabrica.Watch;

            public abstract class CorrelatedObject
            {
                {{retyped}}
            }
            """, "PHLM017");

    [Fact]
    public Task A_private_member_is_left_to_the_using_swap() =>
        MigrationTest.NoneAsync("""
            using Fabrica.Watch;

            public class Orders
            {
                private ILogger _logger;
                private ILogger Next() => _logger;
            }
            """, "PHLM017");

    private const string Helper = """
        using Fabrica.Watch;

        public abstract class CorrelatedObject
        {
            protected {|PHLM017:ILogger|} EnterMethod() => null;
        }
        """;

    // Retyped sooner, EnterMethod() would return a MethodLogger under logger.Debug(…), which would no longer bind to
    // Fabrica.Watch, and no rule would rewrite it.
    [Fact]
    public Task A_logger_handed_to_a_file_not_yet_migrated_waits_for_it()
    {
        var test = new MigrationTest(Helper, Helper, "PHLM017");
        const string caller = """
            using Fabrica.Watch;

            public class Orders : CorrelatedObject
            {
                void Place()
                {
                    var logger = EnterMethod();
                    logger.Debug("Placing");
                }
            }
            """;

        test.TestState.Sources.Add(("/0/Orders.cs", caller));
        test.FixedState.Sources.Add(("/0/Orders.cs", caller));
        return test.RunAsync();
    }

    [Fact]
    public Task A_logger_handed_to_files_already_migrated_is_retyped()
    {
        var test = new MigrationTest(Helper, """
            using Fabrica.Watch;

            public abstract class CorrelatedObject
            {
                protected Pondhawk.Logging.MethodLogger EnterMethod() => null;
            }
            """, "PHLM017");
        const string caller = """
            using Microsoft.Extensions.Logging;

            public class Orders : CorrelatedObject
            {
                void Place()
                {
                    using var logger = EnterMethod();
                    logger.LogDebug("Placing");
                }
            }
            """;

        test.TestState.Sources.Add(("/0/Orders.cs", caller));
        test.FixedState.Sources.Add(("/0/Orders.cs", caller));
        return test.RunAsync();
    }

    // Once nothing is handed out, the usings go.
    [Fact]
    public Task A_retyped_file_then_swaps_its_usings() =>
        new MigrationTest("""
            using Fabrica.Watch;

            public abstract class CorrelatedObject
            {
                protected {|PHLM017:ILogger|} EnterMethod() => null;
                public void Use({|PHLM017:ILogger|} logger) { }
            }
            """, """
            using Microsoft.Extensions.Logging;
            using Pondhawk.Logging;

            public abstract class CorrelatedObject
            {
                protected MethodLogger EnterMethod() => null;
                public void Use(ILogger logger) { }
            }
            """, "PHLM017", "PHLM015")
        {
            NumberOfIncrementalIterations = -4,
            NumberOfFixAllIterations = -3,
        }.RunAsync();
}
