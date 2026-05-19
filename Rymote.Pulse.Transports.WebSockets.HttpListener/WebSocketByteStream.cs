using System.Net.WebSockets;

namespace Rymote.Pulse.Transports.WebSockets.HttpListener;

internal sealed class WebSocketByteStream : Stream
{
    private readonly WebSocket _webSocket;
    private readonly MemoryStream _pendingWriteBuffer = new MemoryStream();

    private byte[] _receiveBuffer;
    private int _receiveOffset;
    private int _receiveLength;
    private bool _receiveEndOfStream;

    public WebSocketByteStream(WebSocket webSocket, int initialReceiveBufferSizeInBytes)
    {
        _webSocket = webSocket;
        _receiveBuffer = new byte[Math.Max(1024, initialReceiveBufferSizeInBytes)];
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

    public override void Flush() => FlushAsync(CancellationToken.None).GetAwaiter().GetResult();

    public override async Task FlushAsync(CancellationToken cancellationToken)
    {
        if (_pendingWriteBuffer.Length == 0) return;

        ArraySegment<byte> bufferSegment = new ArraySegment<byte>(
            _pendingWriteBuffer.GetBuffer(), 0, (int)_pendingWriteBuffer.Length);

        await _webSocket.SendAsync(
            bufferSegment,
            WebSocketMessageType.Binary,
            endOfMessage: true,
            cancellationToken).ConfigureAwait(false);

        _pendingWriteBuffer.SetLength(0);
    }

    public override ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        => _pendingWriteBuffer.WriteAsync(buffer, cancellationToken);

    public override void Write(byte[] buffer, int offset, int count)
        => _pendingWriteBuffer.Write(buffer, offset, count);

    public override Task WriteAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => WriteAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (_receiveEndOfStream) return 0;

        if (_receiveOffset >= _receiveLength)
        {
            _receiveOffset = 0;
            _receiveLength = 0;

            while (true)
            {
                WebSocketReceiveResult receiveResult;
                try
                {
                    int availableSpace = _receiveBuffer.Length - _receiveLength;
                    if (availableSpace == 0)
                    {
                        byte[] grown = new byte[_receiveBuffer.Length * 2];
                        Array.Copy(_receiveBuffer, grown, _receiveLength);
                        _receiveBuffer = grown;
                        availableSpace = _receiveBuffer.Length - _receiveLength;
                    }

                    receiveResult = await _webSocket.ReceiveAsync(
                        new ArraySegment<byte>(_receiveBuffer, _receiveLength, availableSpace),
                        cancellationToken).ConfigureAwait(false);
                }
                catch (WebSocketException)
                {
                    _receiveEndOfStream = true;
                    return 0;
                }

                if (receiveResult.MessageType == WebSocketMessageType.Close)
                {
                    _receiveEndOfStream = true;
                    return 0;
                }

                _receiveLength += receiveResult.Count;
                if (receiveResult.EndOfMessage) break;
            }
        }

        int bytesToCopy = Math.Min(buffer.Length, _receiveLength - _receiveOffset);
        _receiveBuffer.AsMemory(_receiveOffset, bytesToCopy).CopyTo(buffer);
        _receiveOffset += bytesToCopy;
        return bytesToCopy;
    }

    public override int Read(byte[] buffer, int offset, int count)
        => ReadAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing) _pendingWriteBuffer.Dispose();
        base.Dispose(disposing);
    }
}
