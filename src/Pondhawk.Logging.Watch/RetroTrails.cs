// Copyright (c) Pond Hawk Technologies Inc. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System.Diagnostics;
using System.Globalization;
using System.Text;

namespace Pondhawk.Logging.Watch;

/// <summary>
/// The Debug trails of units of work for retro logging. Each trail is a ring of at most
/// <see cref="RetroOptions.MaxEventsPerUnit"/> lines, held as a custom property on the unit of work's
/// <see cref="Activity"/> itself: it lives exactly as long as the unit of work, needs no table, cap or sweep, and is
/// collected with the <see cref="Activity"/> when the unit of work succeeds. An Error releases it into its payload
/// (<see cref="Format"/>). Nothing here throws.
/// </summary>
internal sealed class RetroTrails
{
    /// <summary>The longest a line's text is kept, so a full ring stays small.</summary>
    internal const int MaxTextLength = 500;

    // Watch's own property, beside the mark; not part of the contract with the API.
    private const string TrailProperty = "Pondhawk.RetroLogging.Trail";

    // The width of the payload's banners, as the Context and Exception banners.
    private const int BannerWidth = 53;

    // Guards the first line of a unit of work (create and set the trail) and its release: rare, and short.
    private static readonly Lock TrailLock = new();

    private readonly int _maxLines;
    private long _dropped;

    public RetroTrails(RetroOptions options) =>
        _maxLines = Math.Clamp(options.MaxEventsPerUnit, 1, 10_000);

    /// <summary>Gets the number of lines dropped: the oldest of a full trail, or one added as its trail was sent.</summary>
    public long DroppedCount => Interlocked.Read(ref _dropped);

    /// <summary>Keeps <paramref name="line"/> in the trail of the unit of work <paramref name="holder"/> stands for.</summary>
    public void Hold(Activity holder, TrailLine line)
    {
        try
        {
            if (holder.GetCustomProperty(TrailProperty) is not Trail trail)
            {
                lock (TrailLock)
                {
                    if (holder.GetCustomProperty(TrailProperty) is not Trail existing)
                    {
                        existing = new Trail();
                        holder.SetCustomProperty(TrailProperty, existing);
                    }

                    trail = existing;
                }
            }

            if (!trail.Add(line, _maxLines))
                Interlocked.Increment(ref _dropped);
        }
        catch
        {
            // The trail is a bonus; a failure to keep it never reaches the logging call.
        }
    }

    /// <summary>
    /// Removes the trail <paramref name="holder"/> carries and returns it, oldest line first; empty when it has none. A
    /// later line starts a new trail.
    /// </summary>
    public static ReleasedTrail Release(Activity holder)
    {
        try
        {
            Trail? trail;
            lock (TrailLock)
            {
                trail = holder.GetCustomProperty(TrailProperty) as Trail;
                if (trail is null)
                    return ReleasedTrail.Empty;

                holder.SetCustomProperty(TrailProperty, null);
            }

            return trail.Close();
        }
        catch
        {
            return ReleasedTrail.Empty;
        }
    }

    /// <summary>
    /// The outermost <see cref="Activity"/> of this process that <paramref name="activity"/> runs under — the request,
    /// not a child span such as an outgoing call. A parent in another process is not an <see cref="Activity.Parent"/>.
    /// </summary>
    public static Activity? Outermost(Activity? activity)
    {
        while (activity?.Parent is { } parent)
            activity = parent;

        return activity;
    }

    /// <summary>The last segment of a category: its type's name.</summary>
    public static string LastSegment(string category)
    {
        var dot = category.LastIndexOf('.');
        return dot < 0 || dot == category.Length - 1 ? category : category[(dot + 1)..];
    }

    /// <summary>A line's text, cut at <see cref="MaxTextLength"/>.</summary>
    public static string Cap(string text) =>
        text.Length <= MaxTextLength ? text : string.Concat(text.AsSpan(0, MaxTextLength - 1), "…");

    /// <summary>
    /// The trail as a block of the error's payload: a banner with the count, then one line each — the offset from the
    /// first line, the category's last segment, and the text, indented two spaces per nesting depth.
    /// </summary>
    public static string Format(ReleasedTrail trail) => AppendTo(new StringBuilder(), trail).ToString();

    /// <summary>About how long <see cref="Format"/>'s block is, to size a builder once.</summary>
    public static int EstimateLength(ReleasedTrail trail)
    {
        var length = BannerWidth;
        foreach (var line in trail.Lines)
            length += line.Text.Length + 40;   // offset, padded category, indentation, line break

        return length;
    }

    /// <summary>Writes <see cref="Format"/>'s block to <paramref name="builder"/>, with no line break after it.</summary>
    public static StringBuilder AppendTo(StringBuilder builder, ReleasedTrail trail)
    {
        var lines = trail.Lines;

        var title = trail.Dropped > 0
            ? $"--- Trail ({lines.Count} lines, {trail.Dropped} dropped) "
            : $"--- Trail ({lines.Count} {(lines.Count == 1 ? "line" : "lines")}) ";
        builder.Append(title);
        builder.Append('-', Math.Max(3, BannerWidth - title.Length));

        var width = 0;
        foreach (var line in lines)
            width = Math.Max(width, line.Category.Length);

        var first = lines.Count > 0 ? lines[0].Timestamp : 0;
        var depth = 0;
        foreach (var line in lines)
        {
            // An exit (a negative delta) is shown at its enter's depth; what an enter opens, one deeper. The ring may
            // start inside a method, so the depth never goes below 0.
            if (line.Nesting < 0)
                depth = Math.Max(0, depth + line.Nesting);

            var offset = Stopwatch.GetElapsedTime(first, line.Timestamp).TotalSeconds;
            builder.AppendLine();
            builder.Append('+').Append(CultureInfo.InvariantCulture, $"{offset:0.000}").Append("s  ");
            builder.Append(line.Category).Append(' ', width - line.Category.Length + 2 + (depth * 2));
            builder.Append(line.Text);

            if (line.Nesting > 0)
                depth += line.Nesting;
        }

        return builder;
    }

    private sealed class Trail
    {
        private readonly Lock _lock = new();
        private readonly Queue<TrailLine> _lines = new();
        private int _dropped;
        private bool _closed;

        // False when a line was dropped: the oldest to make room, or this one because the trail was just sent.
        public bool Add(TrailLine line, int max)
        {
            lock (_lock)
            {
                if (_closed)
                    return false;

                _lines.Enqueue(line);
                if (_lines.Count <= max)
                    return true;

                _lines.Dequeue();
                _dropped++;
                return false;
            }
        }

        public ReleasedTrail Close()
        {
            lock (_lock)
            {
                _closed = true;
                var released = new ReleasedTrail([.. _lines], _dropped);
                _lines.Clear();
                return released;
            }
        }
    }
}
