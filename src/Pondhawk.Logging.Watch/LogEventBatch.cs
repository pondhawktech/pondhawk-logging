/*
The MIT License (MIT)

Copyright (c) 2024 Pond Hawk Technologies Inc.

Permission is hereby granted, free of charge, to any person obtaining a copy
of this software and associated documentation files (the "Software"), to deal
in the Software without restriction, including without limitation the rights
to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
copies of the Software, and to permit persons to whom the Software is
furnished to do so, subject to the following conditions:

The above copyright notice and this permission notice shall be included in all
copies or substantial portions of the Software.

THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
SOFTWARE.
*/

using System.Diagnostics.CodeAnalysis;
using MemoryPack;

namespace Pondhawk.Logging.Watch;

/// <summary>
/// Represents a batch of log events for transmission to a sink.
/// </summary>
/// <remarks>
/// <para>
/// Batching improves efficiency by reducing the number of HTTP requests
/// and enabling better compression ratios.
/// </para>
/// <para>
/// Each batch has a unique identifier (Uid) and is associated with a
/// domain for multi-tenant Watch server deployments.
/// </para>
/// </remarks>
[MemoryPackable]
public partial class LogEventBatch
{
    /// <summary>
    /// An empty batch singleton for null-object pattern usage.
    /// </summary>
    public static readonly LogEventBatch Empty = new();

    /// <summary>
    /// Creates a batch containing a single event.
    /// </summary>
    /// <param name="domain">The domain identifier.</param>
    /// <param name="one">The single event to include.</param>
    /// <returns>A new batch containing the event.</returns>
    [SuppressMessage("CA1720", "CA1720:IdentifiersShouldNotContainTypeNames", Justification = "Single is the domain name for creating a batch with one event")]
    public static LogEventBatch Single(string domain, LogEvent one)
    {
        return new LogEventBatch { Domain = domain, Events = new List<LogEvent> { one } };
    }

    /// <summary>
    /// Gets or sets the unique identifier for this batch.
    /// Generated as a ULID for lexicographic sortability.
    /// </summary>
    public string Uid { get; set; } = Ulid.NewUlid().ToString(null, System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>
    /// Gets or sets the domain identifier for multi-tenant deployments.
    /// </summary>
    public string Domain { get; set; } = string.Empty;

    /// <summary>
    /// Gets or sets the list of events in this batch.
    /// </summary>
    public IList<LogEvent> Events { get; set; } = new List<LogEvent>();
}
