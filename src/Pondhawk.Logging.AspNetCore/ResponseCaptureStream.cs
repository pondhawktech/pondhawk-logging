// Copyright (c) Pond Hawk Technologies Inc. All rights reserved.
// Licensed under the MIT License. See LICENSE in the project root for license information.

using Microsoft.AspNetCore.Http;

namespace Pondhawk.Logging.AspNetCore;

/// <summary>
/// Stands in front of a response's body for one request and keeps a copy of the first bytes written through it,
/// for the request dump. Everything written goes straight on to the real body — nothing is held back, so
/// streaming and flushing behave as they did.
/// </summary>
/// <remarks>
/// Whether to keep anything is decided at the first write, when the response's content type is known: only a
/// text body that is not compressed is kept.
/// </remarks>
internal sealed class ResponseCaptureStream : Stream
{
    private readonly Stream _inner;
    private readonly HttpResponse _response;
    private readonly int _cap;
    private readonly MemoryStream _captured = new();
    private bool _decided;

    public ResponseCaptureStream(HttpResponse response, int cap)
    {
        _response = response;
        _inner = response.Body;
        _cap = cap;

        response.Body = this;
    }

    /// <summary>How many bytes the response body ran to.</summary>
    public long Total { get; private set; }

    /// <summary>The response's content type when its body began.</summary>
    public string? ContentType { get; private set; }

    /// <summary>Why the body was not kept, or null when it was.</summary>
    public string? NotWritten { get; private set; }

    /// <summary>The bytes kept: the start of the body, up to the cap.</summary>
    public ReadOnlySpan<byte> Captured => _captured.GetBuffer().AsSpan(0, (int)_captured.Length);

    /// <summary>
    /// Puts the real body back. Anything the endpoint left in the response's pipe is pushed through first: that
    /// pipe was made over this stream, and the server will only finish the one it finds in place afterwards.
    /// </summary>
    public async ValueTask RestoreAsync(bool flush, CancellationToken cancellationToken)
    {
        if (!ReferenceEquals(_response.Body, this))
            return;

        try
        {
            // Only when there is something waiting: a flush starts the response, and with nothing written that is
            // still someone else's to decide (a status-code page, an exception handler).
            if (flush && _response.BodyWriter is { CanGetUnflushedBytes: true, UnflushedBytes: > 0 } pipe)
                await pipe.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _response.Body = _inner;
        }
    }

    public override bool CanRead => false;

    public override bool CanSeek => false;

    public override bool CanWrite => true;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override void Write(byte[] buffer, int offset, int count)
    {
        Keep(buffer.AsSpan(offset, count));
        _inner.Write(buffer, offset, count);
    }

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        Keep(buffer);
        _inner.Write(buffer);
    }

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        Keep(buffer.AsSpan(offset, count));
        return _inner.WriteAsync(buffer, offset, count, cancellationToken);
    }

    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        Keep(buffer.Span);
        return _inner.WriteAsync(buffer, cancellationToken);
    }

    public override void Flush() => _inner.Flush();

    public override Task FlushAsync(CancellationToken cancellationToken) => _inner.FlushAsync(cancellationToken);

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    // The real body is the server's to dispose, not this stream's.
    protected override void Dispose(bool disposing)
    {
        if (disposing)
            _captured.Dispose();

        base.Dispose(disposing);
    }

    private void Keep(ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty)
            return;

        if (!_decided)
        {
            _decided = true;
            ContentType = _response.ContentType;
            NotWritten = HttpDescriber.WhyNotWritten(ContentType, _response.Headers);
        }

        Total += bytes.Length;

        var room = _cap - (int)_captured.Length;

        if (NotWritten is null && room > 0)
            _captured.Write(bytes[..Math.Min(room, bytes.Length)]);
    }
}
