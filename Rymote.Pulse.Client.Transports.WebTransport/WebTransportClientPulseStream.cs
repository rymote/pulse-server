using System.Buffers.Binary;
using System.Net.Quic;
using Rymote.Pulse.Core.Transport;

namespace Rymote.Pulse.Client.Transports.WebTransport;

internal sealed class WebTransportClientPulseStream : IPulseStream
{
    public long StreamId { get; }
    public PulseStreamDirection Direction { get; }
    public bool IsClosed { get; private set; }

    private readonly QuicStream _quicStream;
    private readonly int _maxEnvelopeSizeInBytes;
    private bool _writeCompleted;
    private int _disposed;

    public WebTransportClientPulseStream(
        QuicStream quicStream,
        PulseStreamDirection direction,
        int maxEnvelopeSizeInBytes)
    {
        _quicStream = quicStream;
        StreamId = quicStream.Id;
        Direction = direction;
        _maxEnvelopeSizeInBytes = maxEnvelopeSizeInBytes;
    }

    public async ValueTask<ReadOnlyMemory<byte>?> ReadEnvelopeAsync(CancellationToken cancellationToken)
    {
        byte[] lengthBuffer = new byte[4];
        if (!await ReadExactlyAsync(lengthBuffer, cancellationToken).ConfigureAwait(false))
            return null;

        uint declaredLength = BinaryPrimitives.ReadUInt32BigEndian(lengthBuffer);
        if (declaredLength == 0 || declaredLength > _maxEnvelopeSizeInBytes)
            return null;

        byte[] payload = new byte[(int)declaredLength];
        if (!await ReadExactlyAsync(payload, cancellationToken).ConfigureAwait(false))
            return null;

        return payload;
    }

    private async ValueTask<bool> ReadExactlyAsync(byte[] buffer, CancellationToken cancellationToken)
    {
        int bytesRead = 0;
        while (bytesRead < buffer.Length)
        {
            int chunk;
            try
            {
                chunk = await _quicStream.ReadAsync(
                    buffer.AsMemory(bytesRead, buffer.Length - bytesRead),
                    cancellationToken).ConfigureAwait(false);
            }
            catch (QuicException) { return false; }
            catch (IOException) { return false; }
            catch (ObjectDisposedException) { return false; }
            catch (OperationCanceledException) { return false; }

            if (chunk == 0) return false;
            bytesRead += chunk;
        }
        return true;
    }

    public async ValueTask WriteEnvelopeAsync(ReadOnlyMemory<byte> envelopeFrame, CancellationToken cancellationToken)
    {
        if (_writeCompleted) throw new InvalidOperationException("Stream write side already completed.");
        if (IsClosed) throw new InvalidOperationException("Stream is closed.");
        if (envelopeFrame.Length > _maxEnvelopeSizeInBytes)
            throw new InvalidOperationException(
                $"Envelope ({envelopeFrame.Length}) exceeds MaxStreamEnvelopeSizeInBytes ({_maxEnvelopeSizeInBytes}).");

        byte[] lengthPrefix = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(lengthPrefix, (uint)envelopeFrame.Length);

        await _quicStream.WriteAsync(lengthPrefix.AsMemory(), cancellationToken).ConfigureAwait(false);
        await _quicStream.WriteAsync(envelopeFrame, cancellationToken).ConfigureAwait(false);
        await _quicStream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    public ValueTask CompleteWritesAsync(CancellationToken cancellationToken)
    {
        if (_writeCompleted) return ValueTask.CompletedTask;
        _writeCompleted = true;
        try { _quicStream.CompleteWrites(); }
        catch { /* best-effort */ }
        return ValueTask.CompletedTask;
    }

    public ValueTask AbortAsync(int reasonCode, CancellationToken cancellationToken)
    {
        IsClosed = true;
        try
        {
            _quicStream.Abort(QuicAbortDirection.Both, reasonCode);
        }
        catch { /* best-effort */ }
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        IsClosed = true;

        try { await _quicStream.DisposeAsync().ConfigureAwait(false); }
        catch { /* best-effort */ }

        GC.SuppressFinalize(this);
    }
}
