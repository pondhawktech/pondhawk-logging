// Copyright (c) Pond Hawk Technologies Inc. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System.Collections.Concurrent;

namespace Pondhawk.Logging.Utilities;

/// <summary>
/// Extension methods for Type to get readable type names.
/// </summary>
public static class TypeExtensions
{
    private static readonly ConcurrentDictionary<Type, string> ConciseNameCache = new();
    private static readonly ConcurrentDictionary<Type, string> ConciseFullNameCache = new();

    /// <summary>
    /// Gets a concise, readable name for a type (without namespace).
    /// </summary>
    /// <remarks>
    /// For generic types, produces "List&lt;String&gt;" instead of "List`1".
    /// </remarks>
    public static string GetConciseName(this Type type)
    {
        return ConciseNameCache.GetOrAdd(type, ComputeConciseName);
    }

    /// <summary>
    /// Gets a concise, readable full name for a type (with namespace).
    /// </summary>
    /// <remarks>
    /// For generic types, produces "System.Collections.Generic.List&lt;String&gt;"
    /// instead of "System.Collections.Generic.List`1[[System.String, ...]]".
    /// </remarks>
    public static string GetConciseFullName(this Type type)
    {
        return ConciseFullNameCache.GetOrAdd(type, ComputeConciseFullName);
    }

    private static string ComputeConciseName(Type type)
    {
        var conciseName = type.Name;
        if (!type.IsGenericType)
            return conciseName;

        var iBacktick = conciseName.IndexOf('`');
        if (iBacktick > 0)
            conciseName = conciseName.Substring(0, iBacktick);

        var genericParameters = type.GetGenericArguments().Select(x => x.GetConciseName());
        conciseName += "<" + string.Join(", ", genericParameters) + ">";

        return conciseName;
    }

    private static string ComputeConciseFullName(Type type)
    {
        var conciseName = type.FullName;
        if (string.IsNullOrWhiteSpace(conciseName))
            return string.Empty;

        if (!type.IsGenericType)
            return conciseName;

        var iBacktick = conciseName.IndexOf('`');
        if (iBacktick > 0)
            conciseName = conciseName.Substring(0, iBacktick);

        var genericParameters = type.GetGenericArguments().Select(x => x.GetConciseName());
        conciseName += "<" + string.Join(", ", genericParameters) + ">";

        return conciseName;
    }
}
