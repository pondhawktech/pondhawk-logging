// Copyright (c) Pond Hawk Technologies Inc. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using Microsoft.CodeAnalysis.Testing;
using Pondhawk.Logging.Migration.Tests.Support;
using Xunit;

namespace Pondhawk.Logging.Migration.Tests;

/// <summary>
/// Every rule together on a whole file, as <c>dotnet format</c> runs them: the rewrites first, then the using
/// swap they make possible. The result must compile against Microsoft.Extensions.Logging and Pondhawk.Logging.
/// </summary>
public class WholeFileTests
{
    [Fact]
    public async Task A_class_using_what_step_one_covers_migrates_and_compiles()
    {
        var test = new MigrationTest("""
            using System;
            using Fabrica.Watch;

            namespace Shop;

            public class OrderService
            {
                private readonly ILogger _logger = WatchFactoryLocator.Factory.GetLogger<OrderService>();

                public void Place(int id, Exception problem)
                {
                    using var trace = this.EnterMethod();

                    if ({|PHLM005:_logger.IsDebugEnabled|})
                        {|PHLM001:_logger.Debug("Placing an order")|};

                    {|PHLM001:_logger.Info(id)|};
                    {|PHLM001:_logger.Warning(problem, "Retrying")|};
                    {|PHLM001:_logger.Error(problem)|};
                }
            }
            """, """
            using System;
            using Microsoft.Extensions.Logging;
            using Pondhawk.Logging;

            namespace Shop;

            public class OrderService
            {
                private readonly ILogger _logger = LoggingFactoryLocator.GetFactory().CreateLogger<OrderService>();

                public void Place(int id, Exception problem)
                {
                    using var trace = this.EnterMethod();

                    if (_logger.IsEnabled(LogLevel.Debug))
                        _logger.LogDebug("Placing an order");

                    _logger.LogInformation("{Value}", id);
                    _logger.LogWarning(problem, "Retrying");
                    _logger.LogError(problem, "{Message}", problem.Message);
                }
            }
            """, "PHLM001", "PHLM005", "PHLM012", "PHLM013", "PHLM014", "PHLM015", "PHLM016")
        {
            CompilerDiagnostics = CompilerDiagnostics.Errors,
            NumberOfIncrementalIterations = -10,
            NumberOfFixAllIterations = -4,
        };

        await test.RunAsync();
    }

    [Fact]
    public async Task A_realistic_class_migrates_through_every_rule_together_and_compiles()
    {
        var test = new MigrationTest("""
            using System;
            using Fabrica.Watch;
            using Fabrica.Watch.Sink;

            namespace Shop;

            public class Credentials
            {
                [Sensitive]
                public string Password { get; set; }
            }

            public class OrderService
            {
                private readonly ILogger _logger = WatchFactoryLocator.Factory.GetLogger<OrderService>();

                public void Place(int id, string customer, decimal total, Exception problem, object state)
                {
                    var logger = this.EnterMethod();
                    try
                    {
                        if ({|PHLM005:logger.IsDebugEnabled|})
                            {|PHLM002:logger.Debug("Placing order {0} for {1}", id, customer)|};

                        {|PHLM003:logger.Info($"Total {total:N2}")|};
                        {|PHLM004:logger.Debug(() => "Expensive " + customer)|};
                        logger.Inspect("total", total);
                        {|PHLM011:logger.LogJson("Order", "{}", false)|};
                        {|PHLM010:logger.LogObject("State", state)|};
                        logger.WarningWithContext(problem, state, "Retrying");
                        {|PHLM008:logger.ErrorWithContext(problem, state)|};
                        {|PHLM001:logger.Error(problem)|};
                    }
                    finally
                    {
                        {|PHLM006:logger.LeaveMethod()|};
                    }
                }
            }
            """, """
            using System;
            using Microsoft.Extensions.Logging;
            using Pondhawk.Logging;

            namespace Shop;

            public class Credentials
            {
                [Sensitive]
                public string Password { get; set; }
            }

            public class OrderService
            {
                private readonly ILogger _logger = LoggingFactoryLocator.GetFactory().CreateLogger<OrderService>();

                public void Place(int id, string customer, decimal total, Exception problem, object state)
                {
                    using var logger = this.EnterMethod();
                    if (logger.IsEnabled(LogLevel.Debug))
                        logger.LogDebug("Placing order {Id} for {Customer}", id, customer);

                    logger.LogInformation("Total {Total:N2}", total);
                    if (logger.IsEnabled(LogLevel.Debug)) logger.LogDebug("Expensive {Customer}", customer);
                    logger.Inspect("total", total);
                    logger.LogJson("Order", "{}");
                    logger.LogObject("State", state);
                    logger.WarningWithContext(problem, state, "Retrying");
                    logger.ErrorWithContext(problem, state, problem.Message);
                    logger.LogError(problem, "{Message}", problem.Message);
                }
            }
            """)
        {
            CompilerDiagnostics = CompilerDiagnostics.Errors,
            NumberOfIncrementalIterations = -20,
            NumberOfFixAllIterations = -3,
        };

        await test.RunAsync();
    }
}
