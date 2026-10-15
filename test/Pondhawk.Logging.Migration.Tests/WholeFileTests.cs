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
                private readonly ILogger _logger = {|PHLM012:WatchFactoryLocator.Factory.GetLogger<OrderService>()|};

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
            NumberOfIncrementalIterations = 7,
            NumberOfFixAllIterations = 2,
        };

        await test.RunAsync();
    }
}
