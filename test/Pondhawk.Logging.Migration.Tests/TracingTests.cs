// Copyright (c) Pond Hawk Technologies Inc. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using Pondhawk.Logging.Migration.Tests.Support;
using Xunit;

namespace Pondhawk.Logging.Migration.Tests;

/// <summary>PHLM006 (method tracing) and PHLM007 (scopes).</summary>
public class TracingTests
{
    private static string Code(string body) => $$"""
        using System;
        using Fabrica.Watch;

        class Orders
        {
            void Place(ILogger log, int count)
            {
        {{body}}
            }

            void Save() { }
        }
        """;

    [Fact]
    public Task A_try_finally_that_only_left_the_method_goes_and_the_trace_becomes_a_using() =>
        MigrationTest.RunAsync(Code("""
                    var logger = this.EnterMethod();
                    try
                    {
                        Save();
                    }
                    finally
                    {
                        {|PHLM006:logger.LeaveMethod()|};
                    }
            """), Code("""
                    using var logger = this.EnterMethod();
                    Save();
            """), "PHLM006");

    [Fact]
    public Task Entering_on_an_existing_logger_becomes_a_using_declaration() =>
        MigrationTest.RunAsync(Code("""
                    {|PHLM006:log.EnterMethod()|};
                    Save();
                    {|PHLM006:log.LeaveMethod()|};
            """), Code("""
                    using var _ = log.EnterMethod();
                    Save();
            """), "PHLM006");

    [Fact]
    public Task A_try_with_a_catch_keeps_the_catch() =>
        MigrationTest.RunAsync(Code("""
                    var logger = this.EnterMethod();
                    try
                    {
                        Save();
                    }
                    catch (InvalidOperationException)
                    {
                        throw;
                    }
                    finally
                    {
                        {|PHLM006:logger.LeaveMethod()|};
                    }
            """), Code("""
                    using var logger = this.EnterMethod();
                    try
                    {
                        Save();
                    }
                    catch (InvalidOperationException)
                    {
                        throw;
                    }
            """), "PHLM006");

    [Fact]
    public Task A_LeaveMethod_with_nothing_to_pair_with_goes_and_is_marked() =>
        MigrationTest.RunAsync(Code("""
                    Save();
                    {|PHLM006:log.LeaveMethod()|};
            """), Code("""
                    // TODO(Fabrica.Watch migration): 'log.LeaveMethod()' was removed, but no EnterMethod on 'log' was found to make a using declaration of; check its method trace
                    Save();
            """), "PHLM006");

    [Fact]
    public Task A_scope_becomes_a_using_around_what_it_bracketed() =>
        MigrationTest.RunAsync(Code("""
                    {|PHLM007:log.EnterScope("Pricing")|};
                    Save();
                    Save();
                    {|PHLM007:log.LeaveScope("Pricing")|};
                    Save();
            """), Code("""
                    using (log.EnterMethod("Pricing"))
                    {
                        Save();
                        Save();
                    }

                    Save();
            """), "PHLM007");

    [Fact]
    public Task A_scope_that_declares_what_is_used_after_it_becomes_a_using_declaration_and_is_marked() =>
        MigrationTest.RunAsync(Code("""
                    {|PHLM007:log.EnterScope("Pricing")|};
                    var total = count * 2;
                    {|PHLM007:log.LeaveScope("Pricing")|};
                    Console.WriteLine(total);
            """), Code("""
                    // TODO(Fabrica.Watch migration): 'log.EnterScope' became a using declaration, so the scope now ends with the block rather than at LeaveScope; check where it should end
                    using var scope = log.EnterMethod("Pricing");
                    var total = count * 2;
                    Console.WriteLine(total);
            """), "PHLM007");

    // Found in fabrica-one: a helper that enters a trace and returns the logger for its caller to dispose.
    [Fact]
    public Task An_EnterMethod_nothing_here_closes_is_left_for_whoever_disposes_the_logger() =>
        MigrationTest.NoneAsync(Code("""
                    log.EnterMethod("Place");
                    Save();
            """), "PHLM006");

    // Found in fabrica-one: once rewritten, the EnterMethod carries over, so the usings can go.
    [Fact]
    public Task A_rewritten_trace_lets_the_usings_go() =>
        MigrationTest.RunAsync("""
            using System;
            {|PHLM015:using Fabrica.Watch;|}

            class Orders
            {
                void Place(ILogger log)
                {
                    using var _ = log.EnterMethod();
                }
            }
            """, """
            using System;
            using Microsoft.Extensions.Logging;
            using Pondhawk.Logging;

            class Orders
            {
                void Place(ILogger log)
                {
                    using var _ = log.EnterMethod();
                }
            }
            """, "PHLM015");

    // Found in fabrica-one: every Fabrica.Watch logger is disposable, a Microsoft.Extensions.Logging one is not.
    [Fact]
    public Task A_using_on_a_logger_that_is_not_a_method_trace_goes() =>
        MigrationTest.RunAsync(Code("""
                    {|PHLM006:using var logger = this.GetLogger();|}
                    Save();
            """), Code("""
                    var logger = this.GetLogger();
                    Save();
            """), "PHLM006");

    [Fact]
    public Task A_using_on_a_method_trace_stays() =>
        MigrationTest.NoneAsync(Code("""
                    using var logger = this.EnterMethod();
                    Save();
            """), "PHLM006");

    [Fact]
    public Task A_using_statement_on_a_logger_that_is_not_a_method_trace_becomes_a_block() =>
        MigrationTest.RunAsync(Code("""
                    {|PHLM006:using (var started = this.GetLogger())
                    {
                        Save();
                    }|}
            """), Code("""
                    {
                        var started = this.GetLogger();
                        Save();
                    }
            """), "PHLM006");

    // Found in fabrica-one: a plain logger handed back to Fabrica.Watch's pool with an explicit Dispose.
    [Fact]
    public Task Disposing_a_plain_logger_goes() =>
        MigrationTest.RunAsync(Code("""
                    var logger = this.GetLogger();
                    Save();
                    {|PHLM006:logger.Dispose();|}
                    Save();
            """), Code("""
                    var logger = this.GetLogger();
                    Save();
                    Save();
            """), "PHLM006");

    [Fact]
    public Task Disposing_a_method_trace_stays() =>
        MigrationTest.NoneAsync(Code("""
                    var logger = this.EnterMethod();
                    Save();
                    logger.Dispose();
            """), "PHLM006");

    // Found in fabrica-one: the body of a using statement is not a block, and a rule rewrites it too.
    [Fact]
    public Task A_using_statement_whose_body_is_one_rewritten_call_keeps_the_call() =>
        MigrationTest.RunAsync(Code("""
                    {|PHLM006:using( var started = this.GetLogger() )
                        {|PHLM002:started.Info("Deploying mission ({0})", count)|};|}
            """), Code("""
                    {
                        var started = this.GetLogger();
                        started.LogInformation("Deploying mission ({Count})", count);
                    }
            """), "PHLM006", "PHLM002");
}
