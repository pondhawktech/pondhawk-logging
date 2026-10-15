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
}
