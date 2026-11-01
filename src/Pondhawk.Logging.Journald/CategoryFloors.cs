// Copyright (c) Pond Hawk Technologies Inc. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Pondhawk.Logging.Journald;

/// <summary>
/// The floors the host's provider-less category rules set — <c>AddFilter("Some.Category", LogLevel.Warning)</c>,
/// or <c>Logging:LogLevel:Some.Category</c> — which this provider applies on top of its own rules.
/// </summary>
/// <remarks>
/// <para>
/// Microsoft.Extensions.Logging picks one rule per provider and category, and a rule that names the provider
/// always beats one that names none. The sink's own floor names it, so without this a category floor written
/// the usual way would silently not reach the journal — and those are the floors that keep, say, credentials
/// out of it. An event therefore passes only if both the rule MEL picked for this provider and the floor here
/// let it through.
/// </para>
/// <para>
/// Only rules with a category, a level, no provider and no filter delegate count: not <c>Default</c> or
/// <c>SetMinimumLevel</c>, which name no category, and not Watch's switch filter, which is a delegate — so a
/// Watch switch neither raises nor lowers the journal. The most specific category wins, the last of equals,
/// matched as MEL matches: a prefix, ignoring case, with at most one <c>*</c>. Reloaded configuration applies.
/// </para>
/// </remarks>
internal sealed class CategoryFloors : IDisposable
{
    private readonly IDisposable? _onChange;

    // The rules and the floors worked out from them, replaced together on a reload, so a lookup still running
    // against the old rules cannot leave its floor behind for the new ones.
    private volatile Snapshot _snapshot;

    public CategoryFloors(IOptionsMonitor<LoggerFilterOptions>? options)
    {
        _snapshot = new Snapshot(Relevant(options?.CurrentValue));
        _onChange = options?.OnChange(changed => _snapshot = new Snapshot(Relevant(changed)));
    }

    /// <summary>The lowest level the host's category rules let through for <paramref name="category"/>, or null for none.</summary>
    public LogLevel? Floor(string category)
    {
        var snapshot = _snapshot;
        return snapshot.Floors.GetOrAdd(category, Select, snapshot.Rules);
    }

    private static LogLevel? Select(string category, LoggerFilterRule[] rules)
    {
        LoggerFilterRule? best = null;
        foreach (var rule in rules)
        {
            if (Matches(rule.CategoryName!, category) && (best is null || rule.CategoryName!.Length >= best.CategoryName!.Length))
                best = rule;
        }

        return best?.LogLevel;
    }

    private static LoggerFilterRule[] Relevant(LoggerFilterOptions? options) =>
        options?.Rules
            .Where(r => r.ProviderName is null && r.Filter is null && r.LogLevel is not null
                && !string.IsNullOrEmpty(r.CategoryName) && !string.Equals(r.CategoryName, "Default", StringComparison.OrdinalIgnoreCase))
            .ToArray()
        ?? [];

    // As LoggerRuleSelector matches a rule's category: a prefix, or a prefix and suffix around one '*'.
    private static bool Matches(string pattern, string category)
    {
        var wildcard = pattern.IndexOf('*', StringComparison.Ordinal);
        if (wildcard < 0)
            return category.StartsWith(pattern, StringComparison.OrdinalIgnoreCase);

        if (pattern.IndexOf('*', wildcard + 1) >= 0)
            return false;

        var prefix = pattern.AsSpan(0, wildcard);
        var suffix = pattern.AsSpan(wildcard + 1);
        return category.Length >= prefix.Length + suffix.Length
            && category.AsSpan().StartsWith(prefix, StringComparison.OrdinalIgnoreCase)
            && category.AsSpan().EndsWith(suffix, StringComparison.OrdinalIgnoreCase);
    }

    public void Dispose() => _onChange?.Dispose();

    private sealed record Snapshot(LoggerFilterRule[] Rules)
    {
        public ConcurrentDictionary<string, LogLevel?> Floors { get; } = new(StringComparer.Ordinal);
    }
}
