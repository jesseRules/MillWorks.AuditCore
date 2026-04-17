using System.Security.Cryptography;

namespace MillWorks.AuditCore.Services.Core;

/// <summary>
/// Write-only pass-through <see cref="Stream"/> that records total bytes written and
/// feeds every write into an <see cref="IncrementalHash"/>. Used by the archival pipeline
/// to hash the compressed blob payload as it is streamed to storage, without buffering
/// the full archive in memory.
/// </summary>
internal sealed class CountingHashingStream(
    Stream inner,
    IncrementalHash hash,
    bool leaveOpen = true) : Stream
{
    private long _bytesWritten;

    public long BytesWritten => _bytesWritten;

    public override bool CanRead => false;
    public override bool CanSeek => false;
    public override bool CanWrite => true;
    public override long Length => _bytesWritten;

    public override long Position
    {
        get => _bytesWritten;
        set => throw new NotSupportedException();
    }

    public override void Flush() => inner.Flush();
    public override Task FlushAsync(CancellationToken cancellationToken) => inner.FlushAsync(cancellationToken);

    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count)
    {
        hash.AppendData(buffer, offset, count);
        inner.Write(buffer, offset, count);
        _bytesWritten += count;
    }

    public override void Write(ReadOnlySpan<byte> buffer)
    {
        hash.AppendData(buffer);
        inner.Write(buffer);
        _bytesWritten += buffer.Length;
    }

    public override async Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        hash.AppendData(buffer, offset, count);
        await inner.WriteAsync(buffer.AsMemory(offset, count), cancellationToken).ConfigureAwait(false);
        _bytesWritten += count;
    }

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        hash.AppendData(buffer.Span);
        await inner.WriteAsync(buffer, cancellationToken).ConfigureAwait(false);
        _bytesWritten += buffer.Length;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && !leaveOpen)
        {
            inner.Dispose();
        }

        base.Dispose(disposing);
    }

    public override async ValueTask DisposeAsync()
    {
        if (!leaveOpen)
        {
            await inner.DisposeAsync().ConfigureAwait(false);
        }

        await base.DisposeAsync().ConfigureAwait(false);
    }
}
