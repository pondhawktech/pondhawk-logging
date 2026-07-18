// Copyright (c) Pond Hawk Technologies Inc. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System.Threading;
using Microsoft.Extensions.Logging;

namespace Pondhawk.Logging;

/// <summary>
/// A process-wide locator for the application's <see cref="ILoggerFactory"/>, set once during logging
/// startup. It backs the object logging extensions (<see cref="ObjectLoggingExtensions.GetLogger"/> and
/// <see cref="ObjectLoggingExtensions.EnterMethod"/>) so any object can obtain a logger categorized to its
/// own type without injecting a factory.
/// </summary>
public static class LoggingFactoryLocator
{
    private static ILoggerFactory? _factory;

    /// <summary>
    /// Sets the application logger factory. May be called <em>only once</em> — call it during logging
    /// startup, before anything resolves a logger through this locator.
    /// </summary>
    /// <param name="factory">The logger factory.</param>
    /// <exception cref="ArgumentNullException"><paramref name="factory"/> is <see langword="null"/>.</exception>
    /// <exception cref="InvalidOperationException">The factory has already been set.</exception>
    public static void SetFactory(ILoggerFactory factory)
    {
        ArgumentNullException.ThrowIfNull(factory);

        if (Interlocked.CompareExchange(ref _factory, factory, null) is not null)
        {
            throw new InvalidOperationException(
                "LoggingFactoryLocator.SetFactory has already been called; it may be set only once, during logging startup.");
        }
    }

    /// <summary>Gets the application logger factory set by <see cref="SetFactory"/>.</summary>
    /// <returns>The logger factory.</returns>
    /// <exception cref="InvalidOperationException"><see cref="SetFactory"/> has not been called.</exception>
    public static ILoggerFactory GetFactory()
    {
        return Volatile.Read(ref _factory)
            ?? throw new InvalidOperationException(
                "LoggingFactoryLocator.GetFactory was called before SetFactory; set the factory during logging startup.");
    }

    /// <summary>Clears the locator. A test-only escape hatch for the set-once contract.</summary>
    internal static void Reset() => Interlocked.Exchange(ref _factory, null);
}
