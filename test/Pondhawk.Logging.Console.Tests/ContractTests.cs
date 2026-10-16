// Copyright (c) Pond Hawk Technologies Inc. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using Shouldly;
using Xunit;

namespace Pondhawk.Logging.Console.Tests;

/// <summary>No package may reference Pondhawk.Logging: an application vendors it or drops it.</summary>
public class ContractTests
{
    [Fact]
    public void TheConsole_DoesNotReferencePondhawkLogging() =>
        typeof(JournaldConsoleLoggingBuilderExtensions).Assembly.GetReferencedAssemblies()
            .Select(a => a.Name)
            .ShouldNotContain("Pondhawk.Logging");
}
