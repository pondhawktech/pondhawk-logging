// Copyright (c) Pond Hawk Technologies Inc. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Diagnostics.CodeAnalysis;
using System.Drawing;
using CommunityToolkit.Diagnostics;
using Microsoft.Extensions.Logging;

// ReSharper disable MemberCanBePrivate.Global
// ReSharper disable UnusedMethodReturnValue.Global

namespace Pondhawk.Logging.Watch;

/// <summary>
/// Provides pattern-based switch lookup with version-based cache invalidation.
/// </summary>
/// <remarks>
/// <para>
/// SwitchSource maintains a dictionary of switches keyed by pattern, and a sorted
/// list of patterns for longest-prefix matching. When Update() is called, the
/// switches and patterns are replaced atomically and the Version is incremented.
/// </para>
/// <para>
/// Thread-safety: All public methods are thread-safe. Lookup uses a reader lock
/// while Update uses a writer lock. Version is incremented after the write lock
/// is released to ensure readers see a consistent state.
/// </para>
/// </remarks>
public class SwitchSource : IDisposable
{
    private long _version;
    private readonly ReaderWriterLockSlim _switchLock = new();
    private bool _disposed;
    private Switch _defaultSwitch = new() { Level = LogLevel.Error, Color = Color.LightGray };

    // Each category's level as Lookup last answered it. Replaced (never cleared) after every change that can alter
    // a lookup, so a reader that took the old one before the change only ever adds to a cache nobody reads again.
    private ConcurrentDictionary<string, LogLevel> _levels = NewLevelCache();

    /// <summary>
    /// Gets the current version number. Incremented after each Update() and each change of
    /// <see cref="DefaultSwitch"/>.
    /// </summary>
    public long Version => Volatile.Read(ref _version);

    /// <summary>
    /// Gets or sets the default switch used when no pattern matches.
    /// </summary>
    public Switch DefaultSwitch
    {
        get => Volatile.Read(ref _defaultSwitch);
        set
        {
            Volatile.Write(ref _defaultSwitch, value);
            Changed();
        }
    }

    /// <summary>
    /// Gets or sets the debug switch for explicit debug logging.
    /// </summary>
    public Switch DebugSwitch { get; set; } = new Switch { Level = LogLevel.Debug, Color = Color.PapayaWhip };

    /// <summary>
    /// Gets the ordered list of patterns (longest first).
    /// </summary>
    protected IReadOnlyCollection<string> Patterns { get; set; } = new ReadOnlyCollection<string>(new List<string>());

    /// <summary>
    /// Gets the dictionary of switches keyed by pattern.
    /// </summary>
    protected IDictionary<string, Switch> Switches { get; set; } = new ConcurrentDictionary<string, Switch>(StringComparer.Ordinal);

    /// <summary>
    /// Gets the current switch definitions.
    /// </summary>
    public IList<SwitchDef> CurrentSwitchDefs
    {
        get
        {
            return Switches.Values.Select(s => new SwitchDef
            {
                Pattern = s.Pattern,
                Color = s.Color,
                Level = s.Level,
                Tag = s.Tag
            }).ToList();
        }
    }

    /// <summary>
    /// Configures the default switch with the specified level.
    /// </summary>
    /// <param name="level">The minimum log level for unmatched categories.</param>
    /// <returns>This source for fluent chaining.</returns>
    public SwitchSource WhenNotMatched(LogLevel level)
    {
        var sw = new Switch { Level = level, Color = Color.LightGray };
        DefaultSwitch = sw;
        return this;
    }

    /// <summary>
    /// Configures the default switch with the specified level and color.
    /// </summary>
    /// <param name="level">The minimum log level for unmatched categories.</param>
    /// <param name="color">The color for unmatched categories.</param>
    /// <returns>This source for fluent chaining.</returns>
    public SwitchSource WhenNotMatched(LogLevel level, Color color)
    {
        var sw = new Switch { Level = level, Color = color };
        DefaultSwitch = sw;
        return this;
    }

    /// <summary>
    /// Adds or updates a switch for the specified pattern with default tag.
    /// </summary>
    /// <param name="pattern">The pattern to match.</param>
    /// <param name="level">The minimum log level.</param>
    /// <param name="color">The color for matched categories.</param>
    /// <returns>This source for fluent chaining.</returns>
    public SwitchSource WhenMatched(string pattern, LogLevel level, Color color)
    {
        return WhenMatched(pattern, string.Empty, level, color);
    }

    /// <summary>
    /// Adds or updates a switch for the specified pattern.
    /// </summary>
    /// <param name="pattern">The pattern to match.</param>
    /// <param name="tag">An optional tag.</param>
    /// <param name="level">The minimum log level.</param>
    /// <param name="color">The color for matched categories.</param>
    /// <returns>This source for fluent chaining.</returns>
    public SwitchSource WhenMatched(string pattern, string tag, LogLevel level, Color color)
    {
        var switches = Switches.Select(p => new SwitchDef
        {
            Pattern = p.Value.Pattern,
            Tag = p.Value.Tag,
            Level = p.Value.Level,
            Color = p.Value.Color
        }).ToList();

        var sw = new SwitchDef
        {
            Pattern = pattern,
            Tag = tag,
            Level = level,
            Color = color
        };

        switches.Add(sw);

        Update(switches);

        return this;
    }

    /// <summary>
    /// Starts the switch source. Override for initialization.
    /// </summary>
    public virtual void Start()
    {
    }

    /// <summary>
    /// Stops the switch source. Override for cleanup.
    /// </summary>
    [SuppressMessage("CA1716", "CA1716:IdentifiersShouldNotConflictWithKeywords", Justification = "Stop is the established domain name for lifecycle management in this API")]
    public virtual void Stop()
    {
    }

    /// <summary>
    /// Looks up the switch for a given category.
    /// </summary>
    /// <param name="category">The logger category to match.</param>
    /// <returns>The matching switch or DefaultSwitch.</returns>
    /// <exception cref="ArgumentException">When category is null or whitespace.</exception>
    public virtual Switch Lookup(string category)
    {
        if (string.IsNullOrWhiteSpace(category))
            throw new ArgumentException("Value cannot be null or whitespace.", nameof(category));

        try
        {
            _switchLock.EnterReadLock();
            return FindMatchingSwitch(category) ?? DefaultSwitch;
        }
        finally
        {
            _switchLock.ExitReadLock();
        }
    }

    /// <summary>
    /// The level of the switch <see cref="Lookup"/> finds for <paramref name="category"/>, cached per category: the
    /// switch filter asks this on every <see cref="ILogger.IsEnabled"/>, so it must not take the lock each time.
    /// </summary>
    /// <remarks>
    /// The cache is dropped after <see cref="Update"/> and after <see cref="DefaultSwitch"/> is set. A subclass whose
    /// <see cref="Lookup"/> answers from anything else must call <see cref="Update"/> (or set
    /// <see cref="DefaultSwitch"/>) when that changes.
    /// </remarks>
    internal LogLevel LevelFor(string category)
    {
        var levels = Volatile.Read(ref _levels);
        if (levels.TryGetValue(category, out var level))
            return level;

        level = Lookup(category).Level;
        levels.TryAdd(category, level);
        return level;
    }

    /// <summary>
    /// Looks up just the color for a given category.
    /// </summary>
    /// <param name="category">The logger category to match.</param>
    /// <returns>The color for the matched switch or default.</returns>
    public Color LookupColor(string category)
    {
        if (string.IsNullOrWhiteSpace(category))
            throw new ArgumentException("Value cannot be null or whitespace.", nameof(category));

        try
        {
            _switchLock.EnterReadLock();
            var sw = FindMatchingSwitch(category);
            return sw?.Color ?? DefaultSwitch.Color;
        }
        finally
        {
            _switchLock.ExitReadLock();
        }
    }

    /// <summary>
    /// Gets the default switch.
    /// </summary>
    public Switch GetDefaultSwitch() => DefaultSwitch;

    /// <summary>
    /// Gets the debug switch.
    /// </summary>
    public Switch GetDebugSwitch() => DebugSwitch;

    /// <summary>
    /// Asynchronously updates switches from the underlying source.
    /// Override in derived classes (e.g., WatchSwitchSource) to fetch from remote.
    /// </summary>
    public virtual Task UpdateAsync(CancellationToken ct = default)
    {
        return Task.CompletedTask;
    }

    /// <summary>
    /// Updates the switch configuration. Increments Version after completion.
    /// </summary>
    /// <param name="switchSource">The new switch definitions.</param>
    /// <remarks>
    /// Patterns are sorted by length (longest first) to ensure longest-prefix matching.
    /// Version is incremented after the write lock is released.
    /// </remarks>
    public virtual void Update(IEnumerable<SwitchDef> switchSource)
    {
        Guard.IsNotNull(switchSource);

        var switches = new ConcurrentDictionary<string, Switch>(StringComparer.Ordinal);
        var pKeys = new List<string>();

        foreach (var def in switchSource)
        {
            var sw = new Switch
            {
                Pattern = def.Pattern,
                Tag = def.Tag,
                Color = def.Color,
                Level = def.Level,
            };

            var key = def.Pattern;
            pKeys.Add(key);
            switches[key] = sw;
        }

        var pOrdered = pKeys.OrderBy(k => k.Length).Reverse().ToList();
        var patterns = new ReadOnlyCollection<string>(pOrdered);

        try
        {
            _switchLock.EnterWriteLock();
            Patterns = patterns;
            Switches = switches;
        }
        finally
        {
            _switchLock.ExitWriteLock();
        }

        // After releasing the lock, so readers see a complete, consistent state
        Changed();
    }

    // Drops the level cache and increments Version. Runs after the change is in place, so a reader that takes the
    // new cache also sees the new switches.
    private void Changed()
    {
        Volatile.Write(ref _levels, NewLevelCache());
        Interlocked.Increment(ref _version);
    }

    private static ConcurrentDictionary<string, LogLevel> NewLevelCache() => new(StringComparer.Ordinal);

    /// <summary>
    /// Finds the switch with the longest matching pattern.
    /// </summary>
    /// <param name="category">The category to match.</param>
    /// <returns>The matching switch or null.</returns>
    private Switch? FindMatchingSwitch(string category)
    {
        if (Patterns.Count == 0)
            return null;

        string? match = null;
        var pc = Patterns.Count;
        for (var i = 0; i < pc; i++)
        {
            var pattern = Patterns.ElementAt(i);
            if (!category.StartsWith(pattern, StringComparison.Ordinal))
                continue;

            match = pattern;
            break;
        }

        if (match is null)
            return null;

        return Switches.TryGetValue(match, out var psw) ? psw : null;
    }

    /// <summary>
    /// Releases the resources used by the SwitchSource.
    /// </summary>
    /// <param name="disposing">True if called from Dispose; false if called from a finalizer.</param>
    protected virtual void Dispose(bool disposing)
    {
        if (_disposed)
            return;

        if (disposing)
        {
            _switchLock.Dispose();
        }

        _disposed = true;
    }

    /// <inheritdoc />
    public void Dispose()
    {
        Dispose(true);
        GC.SuppressFinalize(this);
    }
}
