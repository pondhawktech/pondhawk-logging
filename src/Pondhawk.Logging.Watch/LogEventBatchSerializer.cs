// Copyright (c) Pond Hawk Technologies Inc. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using System.Text.Json;
using MemoryPack;
using MemoryPack.Compression;
using Microsoft.IO;

namespace Pondhawk.Logging.Watch;

/// <summary>
/// Provides serialization for LogEventBatch using MemoryPack+Brotli (binary wire format) and JSON.
/// </summary>
/// <remarks>
/// <para>
/// Binary format (MemoryPack+Brotli) is used for wire transmission with excellent compression
/// ratios. JSON format is also provided for debugging and testing.
/// </para>
/// <para>
/// Uses RecyclableMemoryStreamManager for efficient memory pooling.
/// Thread-safe for all operations.
/// </para>
/// </remarks>
public static class LogEventBatchSerializer
{
    private static readonly RecyclableMemoryStreamManager Manager = new();

    /// <summary>
    /// The content type used for the wire format.
    /// </summary>
    public const string ContentType = "application/octet-stream";

    /// <summary>
    /// Serializes a batch to a binary stream using MemoryPack+Brotli compression.
    /// </summary>
    /// <param name="batch">The batch to serialize.</param>
    /// <returns>A stream containing the serialized data.</returns>
    public static async Task<Stream> ToStream(LogEventBatch batch)
    {
        using var compressor = new BrotliCompressor();
        MemoryPackSerializer.Serialize(compressor, batch);

        var stream = Manager.GetStream();
        await compressor.CopyToAsync(stream).ConfigureAwait(false);
        stream.Position = 0;

        return stream;
    }

    /// <summary>
    /// Serializes a batch to an existing stream using MemoryPack+Brotli compression.
    /// </summary>
    /// <param name="batch">The batch to serialize.</param>
    /// <param name="target">The target stream to write to.</param>
    public static async Task ToStream(LogEventBatch batch, Stream target)
    {
        using var compressor = new BrotliCompressor();
        MemoryPackSerializer.Serialize(compressor, batch);
        await compressor.CopyToAsync(target).ConfigureAwait(false);
    }

    /// <summary>
    /// Deserializes a batch from a binary stream.
    /// </summary>
    /// <param name="source">The source stream to read from.</param>
    /// <returns>The deserialized batch, or null if deserialization fails.</returns>
    public static async Task<LogEventBatch?> FromStream(Stream source)
    {
        var stream = Manager.GetStream();
        await using (stream.ConfigureAwait(false))
        {
            await source.CopyToAsync(stream).ConfigureAwait(false);
            stream.Position = 0;

            using var decompressor = new BrotliDecompressor();
            var ros = decompressor.Decompress(stream.GetReadOnlySequence());

            var batch = MemoryPackSerializer.Deserialize<LogEventBatch>(ros);
            return batch;
        }
    }

    /// <summary>
    /// Serializes a batch to a JSON string.
    /// </summary>
    /// <param name="batch">The batch to serialize.</param>
    /// <returns>The JSON representation.</returns>
    public static string ToJson(LogEventBatch batch)
    {
        return JsonSerializer.Serialize(batch, LogEventBatchContext.Default.LogEventBatch);
    }

    /// <summary>
    /// Deserializes a batch from a JSON string.
    /// </summary>
    /// <param name="json">The JSON string.</param>
    /// <returns>The deserialized batch, or null if deserialization fails.</returns>
    public static LogEventBatch? FromJson(string json)
    {
        return JsonSerializer.Deserialize(json, LogEventBatchContext.Default.LogEventBatch);
    }
}
