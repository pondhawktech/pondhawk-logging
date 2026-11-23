// Copyright (c) Pond Hawk Technologies Inc. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System.Diagnostics;

namespace Pondhawk.Logging.Watch;

/// <summary>One kept Debug event: what its trail line shows, and no more.</summary>
/// <param name="Timestamp">When it was logged (<see cref="Stopwatch.GetTimestamp"/>).</param>
/// <param name="Category">The category's last segment.</param>
/// <param name="Nesting">The nesting delta the event carried: +1 entering a method, -1 leaving it.</param>
/// <param name="Text">The message, or a payload's title; at most <see cref="RetroTrails.MaxTextLength"/> characters.</param>
internal readonly record struct TrailLine(long Timestamp, string Category, int Nesting, string Text);
