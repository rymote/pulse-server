using System.Threading.Channels;

namespace Rymote.Pulse.Tests.Helpers;

internal sealed class InMemoryDuplexStream : Stream
{
    private readonly Channel<byte[]> _incoming;
    private readonly Channel<byte[]> _outgoing;

    private byte[]? _pendingChunk;
    private int _pendingChunkOffset;

    public InMemoryDuplexStream(Channel<byte[]> incoming, Channel<byte[]> outgoing)
    {
        _incoming = incoming;
        _outgoing = outgoing;
    }

    public override bool CanRead => true;
    public override bool CanWrite => true;
    public override bool CanSeek => false;
    public override long Length => throw new NotSupportedException();
    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (_pendingChunk == null || _pendingChunkOffset >= _pendingChunk.Length)
        {
            try
            {
                if (!await _incoming.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
                    return 0;
                _pendingChunk = await _incoming.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (ChannelClosedException)
            {
                return 0;
            }
            _pendingChunkOffset = 0;
        }

        int bytesToCopy = Math.Min(buffer.Length, _pendingChunk.Length - _pendingChunkOffset);
        _pendingChunk.AsMemory(_pendingChunkOffset, bytesToCopy).CopyTo(buffer);
        _pendingChunkOffset += bytesToCopy;
        return bytesToCopy;
    }

    public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
    {
        byte[] copy = buffer.ToArray();
        await _outgoing.Writer.WriteAsync(copy, cancellationToken).ConfigureAwait(false);
    }

    public override int Read(byte[] buffer, int offset, int count)
        => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override void Write(byte[] buffer, int offset, int count)
        => WriteAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override Task FlushAsync(CancellationToken cancellationToken) => Task.CompletedTask;
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _outgoing.Writer.TryComplete();
        }
        base.Dispose(disposing);
    }
}
