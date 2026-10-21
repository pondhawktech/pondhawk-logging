// Copyright (c) Pond Hawk Technologies Inc. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using Pondhawk.Logging.Migration.Tests.Support;
using Xunit;

namespace Pondhawk.Logging.Migration.Tests;

/// <summary>
/// PHLM002 (positional templates), PHLM003 (messages built at the call) and PHLM004 (lazy messages).
/// </summary>
public class MessageTests
{
    private static string Code(string body) => $$"""
        using System;
        using Fabrica.Watch;

        class Order { public int Id; public Customer Customer; public decimal Total; }
        class Customer { public int Id; public Order Order; }
        class Shop { public Order Order; }

        class Orders
        {
            private Order order;

            void Place(ILogger logger, Exception ex, Order order, Customer customer, Shop x, Shop y, int count, string name, string format, object[] values)
            {
                {{body}}
            }

            int Next() => 1;
        }
        """;

    private const string Todo = "// TODO(Fabrica.Watch migration): ";

    // PHLM002

    [Theory]
    [InlineData("""logger.Debug("Placed {0} for {1}", count, name)""", """logger.LogDebug("Placed {Count} for {Name}", count, name)""")]
    [InlineData("""logger.Info("Total {0,-10:N2}", order.Total)""", """logger.LogInformation("Total {OrderTotal,-10:N2}", order.Total)""")]
    [InlineData("""logger.Debug("Order {0} for {1}", order.Id, customer.Id)""", """logger.LogDebug("Order {OrderId} for {CustomerId}", order.Id, customer.Id)""")]
    [InlineData("""logger.Debug("Placed {0}", 42)""", """logger.LogDebug("Placed {Arg0}", 42)""")]
    [InlineData("""logger.Debug("{{literal}} {0}", count)""", """logger.LogDebug("{{literal}} {Count}", count)""")]
    [InlineData("""logger.Debug("Placed {0}", count, name)""", """logger.LogDebug("Placed {Count}", count)""")]
    [InlineData("""logger.Debug("{0} then {0}", count)""", """logger.LogDebug("{Count} then {Count}", count, count)""")]
    [InlineData("""logger.Error(ex, "Failed {0}", order.Id)""", """logger.LogError(ex, "Failed {OrderId}", order.Id)""")]
    public Task A_positional_template_gets_named_holes(string fabrica, string logging) =>
        MigrationTest.RunAsync(Code("{|PHLM002:" + fabrica + "|};"), Code(logging + ";"), "PHLM002");

    // Found in fabrica-one: its copy of Fabrica.Watch names the template overloads DebugFormat, WarningFormat, ….
    [Theory]
    [InlineData("""logger.WarningFormat("Appliance {0} did not stop", name)""", """logger.LogWarning("Appliance {Name} did not stop", name)""")]
    [InlineData("""logger.DebugFormat("Placed {0}", count)""", """logger.LogDebug("Placed {Count}", count)""")]
    public Task A_renamed_template_overload_is_a_template_too(string fabrica, string logging) =>
        MigrationTest.RunAsync(Code("{|PHLM002:" + fabrica + "|};"), Code(logging + ";"), "PHLM002");

    [Theory]
    [InlineData("""logger.Error(ex, "Caught {0} in {1}", ex.GetType().FullName!, GetType().FullName!)""", """logger.LogError(ex, "Caught {ExType} in {Type}", ex.GetType().FullName, GetType().FullName)""")]
    [InlineData("""logger.Debug("Type {0}", x?.GetType().Name)""", """logger.LogDebug("Type {XType}", x?.GetType().Name)""")]
    [InlineData("""logger.Info("Order {0} for {1}", customer?.Id, customer?.Order.Id ?? 0)""", """logger.LogInformation("Order {CustomerId} for {OrderId}", customer?.Id, customer?.Order.Id ?? 0)""")]
    [InlineData("""logger.Warning("{0} failed: {1}", name, ex.InnerException?.GetBaseException().Message ?? name)""", """logger.LogWarning("{Name} failed: {Message}", name, ex.InnerException?.GetBaseException().Message ?? name)""")]
    [InlineData("""logger.Debug("Placed {0} of {1}", (object)count, name!)""", """logger.LogDebug("Placed {Count} of {Name}", (object)count, name)""")]
    [InlineData("""logger.Debug("Placed {0}", Next() + 1)""", """logger.LogDebug("Placed {Arg0}", Next() + 1)""")]
    public Task A_hole_is_named_through_null_checks_calls_and_GetType(string fabrica, string logging) =>
        MigrationTest.RunAsync(Code("{|PHLM002:" + fabrica + "|};"), Code(logging + ";"), "PHLM002");

    [Fact]
    public Task Different_values_that_name_alike_take_more_of_their_name_then_a_number() =>
        MigrationTest.RunAsync(
            Code("""{|PHLM002:logger.Debug("{0} {1} {2} {3}", x.Order.Id, y.Order.Id, this.order.Id, order.Id)|};"""),
            Code("""logger.LogDebug("{XOrderId} {YOrderId} {OrderId} {OrderId2}", x.Order.Id, y.Order.Id, this.order.Id, order.Id);"""),
            "PHLM002");

    [Fact]
    public Task A_value_used_twice_that_is_not_cheap_to_evaluate_is_marked() =>
        MigrationTest.RunAsync(
            Code("""{|PHLM002:logger.Debug("{0} then {0}", Next())|};"""),
            Code(Todo + "'Next()' fills 2 holes and is now evaluated 2 times\n        " + """logger.LogDebug("{Arg0} then {Arg0}", Next(), Next());"""),
            "PHLM002");

    [Theory]
    [InlineData("""logger.Debug(format, count)""", """logger.LogDebug("{Message}", string.Format(format, count))""")]
    [InlineData("""logger.Debug("Placed {0}", values)""", """logger.LogDebug("{Message}", string.Format("Placed {0}", values))""")]
    [InlineData("""logger.Debug("Placed {3}", count)""", """logger.LogDebug("{Message}", string.Format("Placed {3}", count))""")]
    public Task A_template_that_cannot_be_read_is_formatted_as_before_and_marked(string fabrica, string logging) =>
        MigrationTest.RunAsync(
            Code("{|PHLM002:" + fabrica + "|};"),
            Code(Todo + "the template could not be turned into a message template, so it is formatted as before; give it named holes\n        " + logging + ";"),
            "PHLM002");

    // PHLM003

    [Theory]
    [InlineData("""logger.Debug($"Placed {count} for {order.Id:N0}")""", """logger.LogDebug("Placed {Count} for {OrderId:N0}", count, order.Id)""")]
    [InlineData("""logger.Info("Placed " + count + " items for " + name)""", """logger.LogInformation("Placed {Count} items for {Name}", count, name)""")]
    [InlineData("""logger.Debug($"{{json}} {count}")""", """logger.LogDebug("{{json}} {Count}", count)""")]
    [InlineData("""logger.Error(ex, $"Failed {name}")""", """logger.LogError(ex, "Failed {Name}", name)""")]
    public Task A_message_built_at_the_call_becomes_a_template(string fabrica, string logging) =>
        MigrationTest.RunAsync(Code("{|PHLM003:" + fabrica + "|};"), Code(logging + ";"), "PHLM003");

    // PHLM004

    [Theory]
    [InlineData("""logger.Debug(() => $"Placed {count}")""", """if (logger.IsEnabled(Microsoft.Extensions.Logging.LogLevel.Debug)) logger.LogDebug("Placed {Count}", count)""")]
    [InlineData("""logger.Info(() => "Placed")""", """if (logger.IsEnabled(Microsoft.Extensions.Logging.LogLevel.Information)) logger.LogInformation("Placed")""")]
    [InlineData("""logger.Debug(() => name)""", """if (logger.IsEnabled(Microsoft.Extensions.Logging.LogLevel.Debug)) logger.LogDebug("{Message}", name)""")]
    [InlineData("""logger.Debug(() => { return name; })""", """if (logger.IsEnabled(Microsoft.Extensions.Logging.LogLevel.Debug)) logger.LogDebug("{Message}", new System.Func<string>(() => { return name; })())""")]
    public Task A_lazy_message_stays_lazy_behind_a_level_check(string fabrica, string logging) =>
        MigrationTest.RunAsync(Code("{|PHLM004:" + fabrica + "|};"), Code(logging + ";"), "PHLM004");
}
