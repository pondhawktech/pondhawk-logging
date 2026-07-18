// Copyright (c) Pond Hawk Technologies Inc. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System.Runtime.CompilerServices;
using Microsoft.Extensions.Logging;
using Pondhawk.Logging.Utilities;

namespace Pondhawk.Logging;

/// <summary>
/// Extensions that let any object obtain a logger categorized to its own runtime type, using the
/// process-wide <see cref="LoggingFactoryLocator"/>. This lets a type log without injecting an
/// <see cref="ILoggerFactory"/> — at the cost of these methods appearing on every type. Requires
/// <see cref="LoggingFactoryLocator.SetFactory"/> to have been called during logging startup.
/// </summary>
public static class ObjectLoggingExtensions
{
    /// <summary>
    /// Gets a logger whose category is the concise full name of <paramref name="instance"/>'s runtime type.
    /// </summary>
    /// <param name="instance">The object whose type names the logger category.</param>
    /// <returns>A logger categorized to the instance's runtime type.</returns>
    public static ILogger GetLogger(this object instance)
    {
        ArgumentNullException.ThrowIfNull(instance);

        var category = instance.GetType().GetConciseFullName();
        return LoggingFactoryLocator.GetFactory().CreateLogger(category);
    }

    /// <summary>
    /// Opens a method-tracing scope on a logger categorized to <paramref name="instance"/>'s runtime type —
    /// equivalent to <c>instance.GetLogger().EnterMethod()</c>.
    /// </summary>
    /// <param name="instance">The object whose type names the logger category.</param>
    /// <param name="method">The calling method name (auto-populated by the compiler).</param>
    /// <returns>A disposable <see cref="MethodLogger"/> that also implements <see cref="ILogger"/>.</returns>
    public static MethodLogger EnterMethod(this object instance, [CallerMemberName] string method = "")
    {
        return instance.GetLogger().EnterMethod(method);
    }
}
