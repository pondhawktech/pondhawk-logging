// Copyright (c) Pond Hawk Technologies Inc. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

namespace Pondhawk.Logging.Watch;

/// <summary>A trail as released: its lines, oldest first, and how many older ones the ring dropped.</summary>
internal sealed record ReleasedTrail(IReadOnlyList<TrailLine> Lines, int Dropped)
{
    public static ReleasedTrail Empty { get; } = new([], 0);

    /// <summary>Two trails as one, in time order: a unit of work can have one on its mark and one on its request.</summary>
    public ReleasedTrail Merge(ReleasedTrail other) =>
        other.Lines.Count == 0 ? this
        : Lines.Count == 0 ? other
        : new ReleasedTrail([.. Lines.Concat(other.Lines).OrderBy(l => l.Timestamp)], Dropped + other.Dropped);
}
