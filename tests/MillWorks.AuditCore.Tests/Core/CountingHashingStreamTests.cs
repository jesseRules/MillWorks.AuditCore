using System.Security.Cryptography;
using FluentAssertions;
using MillWorks.AuditCore.Services.Core;

namespace MillWorks.AuditCore.Tests.Core;

[TestFixture]
[Category("Unit")]
public sealed class CountingHashingStreamTests
{
    [Test]
    public void Write_Sync_UpdatesHashAndCountAfterInnerWriteSucceeds()
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using var inner = new MemoryStream();
        using var sut = new CountingHashingStream(inner, hash);

        var data = "Hello, World!"u8.ToArray();
        sut.Write(data, 0, data.Length);

        sut.BytesWritten.Should().Be(data.Length);
        inner.Length.Should().Be(data.Length);
    }

    [Test]
    public void Write_Sync_HashMatchesActualBytesWrittenOnSuccess()
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using var referenceHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using var inner = new MemoryStream();
        using var sut = new CountingHashingStream(inner, hash);

        var data = "Test data for hashing"u8.ToArray();
        sut.Write(data, 0, data.Length);

        referenceHash.AppendData(data);
        var expected = referenceHash.GetHashAndReset();
        var actual = hash.GetHashAndReset();

        actual.Should().BeEquivalentTo(expected);
    }

    [Test]
    public void Write_Sync_PartialWriteFailure_HashNotUpdated()
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var failingStream = new FailingStream(failAfterBytes: 5);
        using var sut = new CountingHashingStream(failingStream, hash, leaveOpen: true);

        var data = new byte[10];
        Array.Fill(data, (byte)'A');

        var act = () => sut.Write(data, 0, data.Length);

        act.Should().Throw<IOException>();
        sut.BytesWritten.Should().Be(0);
    }

    [Test]
    public async Task WriteAsync_UpdatesHashAndCountAfterInnerWriteSucceeds()
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using var inner = new MemoryStream();
        using var sut = new CountingHashingStream(inner, hash);

        var data = "Async test data"u8.ToArray();
        await sut.WriteAsync(data, 0, data.Length);

        sut.BytesWritten.Should().Be(data.Length);
        inner.Length.Should().Be(data.Length);
    }

    [Test]
    public async Task WriteAsync_Memory_UpdatesHashAndCountAfterInnerWriteSucceeds()
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using var inner = new MemoryStream();
        using var sut = new CountingHashingStream(inner, hash);

        var data = "Memory overload test"u8.ToArray();
        await sut.WriteAsync(data.AsMemory());

        sut.BytesWritten.Should().Be(data.Length);
        inner.Length.Should().Be(data.Length);
    }

    [Test]
    public async Task WriteAsync_PartialWriteFailure_HashNotUpdated()
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var failingStream = new FailingStream(failAfterBytes: 5);
        using var sut = new CountingHashingStream(failingStream, hash, leaveOpen: true);

        var data = new byte[10];
        Array.Fill(data, (byte)'B');

        var act = async () => await sut.WriteAsync(data.AsMemory());

        await act.Should().ThrowAsync<IOException>();
        sut.BytesWritten.Should().Be(0);
    }

    [Test]
    public void Write_Span_UpdatesHashAndCountAfterInnerWriteSucceeds()
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using var inner = new MemoryStream();
        using var sut = new CountingHashingStream(inner, hash);

        ReadOnlySpan<byte> data = "Span test data"u8;
        sut.Write(data);

        sut.BytesWritten.Should().Be(data.Length);
        inner.Length.Should().Be(data.Length);
    }

    [Test]
    public void MultipleWrites_HashMatchesAccumulated()
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using var referenceHash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using var inner = new MemoryStream();
        using var sut = new CountingHashingStream(inner, hash);

        var data1 = "First chunk"u8.ToArray();
        var data2 = "Second chunk"u8.ToArray();
        var data3 = "Third chunk"u8.ToArray();

        sut.Write(data1, 0, data1.Length);
        sut.Write(data2, 0, data2.Length);
        sut.Write(data3, 0, data3.Length);

        referenceHash.AppendData(data1);
        referenceHash.AppendData(data2);
        referenceHash.AppendData(data3);

        var expected = referenceHash.GetHashAndReset();
        var actual = hash.GetHashAndReset();

        actual.Should().BeEquivalentTo(expected);
        sut.BytesWritten.Should().Be(data1.Length + data2.Length + data3.Length);
    }

    private sealed class FailingStream : Stream
    {
        private readonly int _failAfterBytes;
        private int _bytesWritten;

        public FailingStream(int failAfterBytes)
        {
            _failAfterBytes = failAfterBytes;
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            if (_bytesWritten + count > _failAfterBytes)
            {
                throw new IOException("Simulated write failure");
            }
            _bytesWritten += count;
        }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            if (_bytesWritten + buffer.Length > _failAfterBytes)
            {
                throw new IOException("Simulated write failure");
            }
            _bytesWritten += buffer.Length;
        }

        public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (_bytesWritten + buffer.Length > _failAfterBytes)
            {
                return ValueTask.FromException(new IOException("Simulated write failure"));
            }
            _bytesWritten += buffer.Length;
            return ValueTask.CompletedTask;
        }

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => _bytesWritten;
        public override long Position { get => _bytesWritten; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }
}
