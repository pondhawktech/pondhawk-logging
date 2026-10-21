// Copyright (c) Pond Hawk Technologies Inc. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

namespace Pondhawk.Logging.CloudWatch;

/// <summary>
/// Which categories one <c>AddCloudWatch</c> registration sends: all of them, only those named, or all but
/// those named. A name matches as a <c>Microsoft.Extensions.Logging</c> filter rule's category does, ignoring
/// case: the category itself, or any category it is a dotted prefix of — <c>Orders.Access</c> matches
/// <c>Orders.Access.Bff</c> but not <c>Orders.AccessX</c>.
/// </summary>
internal sealed class CategoryRoute
{
    /// <summary>Every category.</summary>
    public static readonly CategoryRoute All = new([], only: false);

    private readonly string[] _names;
    private readonly bool _only;

    public CategoryRoute(IEnumerable<string> names, bool only)
    {
        _names = names.ToArray();
        _only = only;
    }

    /// <summary>Whether events of <paramref name="category"/> go to this registration's log group.</summary>
    public bool Includes(string category) => _only == _names.Any(name => Matches(name, category));

    private static bool Matches(string name, string category) =>
        category.StartsWith(name, StringComparison.OrdinalIgnoreCase)
        && (category.Length == name.Length || category[name.Length] == '.');
}
