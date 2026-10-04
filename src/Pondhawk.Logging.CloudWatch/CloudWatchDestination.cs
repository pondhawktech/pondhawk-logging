// Copyright (c) Pond Hawk Technologies Inc. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System.Text.RegularExpressions;

namespace Pondhawk.Logging.CloudWatch;

/// <summary>
/// The CloudWatch Logs group the provider currently writes to, rebindable while the process runs.
/// </summary>
/// <remarks>
/// <para>
/// Hold one of these, pass it to <c>AddCloudWatch</c>, and call <see cref="Rebind"/> when the group
/// changes. It is also registered as a singleton by <c>AddCloudWatch</c>, so it can be resolved from DI
/// rather than captured.
/// </para>
/// <para>
/// A rebind tears nothing down: the delivery channel, the held events and the AWS client all survive it.
/// The provider creates its stream in the new group on the next batch, and a group it had been denied —
/// or was pausing after a failure on — no longer holds the new one shut.
/// </para>
/// <para>
/// A destination can also start <see cref="Unbound"/>: a host whose group arrives later — from a mission
/// plan, from user-data — has nothing to name at startup. While unbound the provider calls nothing and
/// counts no failure, holding the most recent Warning-and-above events so those describing how the process
/// came up reach CloudWatch once a group is named.
/// </para>
/// <para>Thread-safe: readers take a consistent snapshot, and rebinds are serialized.</para>
/// </remarks>
public sealed partial class CloudWatchDestination
{
    /// <summary>The longest name CloudWatch Logs accepts for a log group.</summary>
    public const int MaxLogGroupLength = 512;

    private readonly object _rebindLock = new();
    private Binding _current;

    /// <summary>Creates a destination bound to a log group.</summary>
    /// <param name="logGroup">The log group name, e.g. <c>/pondhawk-one/PartnerConnect-Production</c>.</param>
    /// <exception cref="ArgumentException"><paramref name="logGroup"/> is not a valid log group name.</exception>
    public CloudWatchDestination(string logGroup)
    {
        _current = new Binding(1, Validate(logGroup));
    }

    private CloudWatchDestination(Binding binding) => _current = binding;

    /// <summary>
    /// Creates a destination that names no log group yet. Nothing is sent until <see cref="Rebind"/> names
    /// one; the most recent events at Warning and above are held until then, and lower levels are discarded.
    /// </summary>
    /// <returns>An unbound destination, ready to be passed to <c>AddCloudWatch</c> and rebound later.</returns>
    public static CloudWatchDestination Unbound()
        => new(new Binding(1, string.Empty));

    /// <summary>Gets the current log group name, or an empty string when unbound.</summary>
    public string LogGroup => Current.LogGroup;

    /// <summary>
    /// Gets the current binding's version. It starts at 1 and increments on every <see cref="Rebind"/>
    /// that changes something, which is how the processor notices a rebind.
    /// </summary>
    public long Version => Current.Version;

    /// <summary>
    /// Gets whether a log group has been named. <see langword="false"/> only for an <see cref="Unbound"/>
    /// destination that has not been rebound yet.
    /// </summary>
    public bool IsBound => !Current.IsUnbound;

    /// <summary>Points the provider at a different log group, effective from the next batch sent.</summary>
    /// <param name="logGroup">The log group name to write to.</param>
    /// <returns>
    /// <see langword="true"/> when the destination changed; <see langword="false"/> when it already named
    /// this group, in which case nothing is disturbed. Callers handed a configuration that repeats the same
    /// group can therefore call this unconditionally.
    /// </returns>
    /// <exception cref="ArgumentException"><paramref name="logGroup"/> is not a valid log group name.</exception>
    public bool Rebind(string logGroup)
    {
        var validated = Validate(logGroup);

        lock (_rebindLock)
        {
            var current = _current;

            if (string.Equals(validated, current.LogGroup, StringComparison.Ordinal))
                return false;

            Volatile.Write(ref _current, new Binding(current.Version + 1, validated));
            return true;
        }
    }

    /// <summary>
    /// Makes a log group name out of arbitrary text by replacing every character CloudWatch Logs does not
    /// allow in one with <c>-</c>, so a name built from an application or environment name is always usable.
    /// </summary>
    /// <param name="logGroup">The text to make a log group name from.</param>
    /// <returns>The sanitized name, cut to <see cref="MaxLogGroupLength"/>.</returns>
    public static string Sanitize(string logGroup)
    {
        ArgumentNullException.ThrowIfNull(logGroup);

        var sanitized = NotAllowed().Replace(logGroup.Trim(), "-");
        return sanitized.Length > MaxLogGroupLength ? sanitized[..MaxLogGroupLength] : sanitized;
    }

    /// <summary>Gets the current binding as one consistent snapshot of version and group.</summary>
    internal Binding Current => Volatile.Read(ref _current);

    private static string Validate(string logGroup)
    {
        if (string.IsNullOrWhiteSpace(logGroup))
            throw new ArgumentException("A CloudWatch log group name is required.", nameof(logGroup));

        if (logGroup.Length > MaxLogGroupLength || NotAllowed().IsMatch(logGroup))
        {
            throw new ArgumentException(
                $"'{logGroup}' is not a valid CloudWatch log group name: up to {MaxLogGroupLength} characters from a-z, A-Z, 0-9, '_', '-', '/', '.' and '#'. CloudWatchDestination.Sanitize makes one from arbitrary text.",
                nameof(logGroup));
        }

        return logGroup;
    }

    [GeneratedRegex("[^.\\-_/#A-Za-z0-9]", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex NotAllowed();

    /// <summary>One immutable view of the destination, read once per batch.</summary>
    internal sealed class Binding
    {
        public Binding(long version, string logGroup)
        {
            Version = version;
            LogGroup = logGroup;
        }

        public long Version { get; }

        public string LogGroup { get; }

        /// <summary>True when no log group has been named yet, so there is nowhere to send.</summary>
        public bool IsUnbound => LogGroup.Length == 0;
    }
}
