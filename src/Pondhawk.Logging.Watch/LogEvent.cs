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
using System.Text.Json.Serialization;
using MemoryPack;

namespace Pondhawk.Logging.Watch;

/// <summary>
/// Represents a single log event with all associated metadata and payload.
/// </summary>
/// <remarks>
/// <para>
/// LogEvent is the core data structure that flows through the logging pipeline:
/// Logger -> Channel -> Batch -> Sink
/// </para>
/// <para>
/// Fields are optimized for MemoryPack serialization. Non-serializable fields
/// (Object, Error, ErrorContext) are marked with MemoryPackIgnore and are
/// processed by the serializers before transmission.
/// </para>
/// <para>
/// Thread-safety: LogEvent instances are not thread-safe. Each event should
/// be created and populated by a single thread before being queued.
/// </para>
/// </remarks>
[MemoryPackable]
public partial class LogEvent
{
    /// <summary>
    /// Gets the logger category (typically a fully-qualified type name).
    /// </summary>
    public string Category { get; set; } = string.Empty;

    /// <summary>
    /// Gets the correlation ID for request tracing.
    /// Uses Activity.Current.TraceId if available, otherwise a new ULID.
    /// </summary>
    public string CorrelationId { get; set; } = string.Empty;

    /// <summary>
    /// Gets the formatted log message.
    /// </summary>
    public string Title { get; set; } = string.Empty;

    /// <summary>
    /// Gets the tenant identifier for multi-tenant applications.
    /// </summary>
    public string Tenant { get; set; } = string.Empty;

    /// <summary>
    /// Gets the subject identifier (e.g., user ID, session ID).
    /// </summary>
    public string Subject { get; set; } = string.Empty;

    /// <summary>
    /// Gets the tag from the matching switch.
    /// </summary>
    public string Tag { get; set; } = string.Empty;

    /// <summary>
    /// Gets the log level as an integer (maps to Level enum).
    /// </summary>
    public int Level { get; set; }

    /// <summary>
    /// Gets the ARGB color value from the matching switch.
    /// </summary>
    public int Color { get; set; }

    /// <summary>
    /// Gets the nesting level for method tracing.
    /// +1 = method entry, -1 = method exit, 0 = normal log.
    /// </summary>
    public int Nesting { get; set; }

    /// <summary>
    /// Gets the UTC timestamp when the event occurred.
    /// </summary>
    public DateTime Occurred { get; set; }

    /// <summary>
    /// Gets the payload type as an integer (maps to PayloadType enum).
    /// </summary>
    public int Type { get; set; }

    /// <summary>
    /// Gets the serialized payload content (JSON, SQL, XML, etc.).
    /// </summary>
    public string? Payload { get; set; }

    /// <summary>
    /// Gets or sets the exception type name when an error is logged.
    /// </summary>
    public string? ErrorType { get; set; }

    /// <summary>
    /// Gets or sets the object to serialize as payload.
    /// Not serialized - processed by IWatchObjectSerializer before transmission.
    /// </summary>
    [JsonIgnore]
    [MemoryPackIgnore]
    [SuppressMessage("CA1720", "CA1720:IdentifiersShouldNotContainTypeNames", Justification = "Object is the established domain name for this property in the Watch logging pipeline")]
    public object? Object { get; set; }

    /// <summary>
    /// Gets or sets the exception to serialize.
    /// Not serialized - processed by IWatchExceptionSerializer before transmission.
    /// </summary>
    [JsonIgnore]
    [MemoryPackIgnore]
    public Exception? Error { get; set; }

    /// <summary>
    /// Gets or sets additional context for exception serialization.
    /// Not serialized - used during exception processing.
    /// </summary>
    [JsonIgnore]
    [MemoryPackIgnore]
    public object? ErrorContext { get; set; }
}
