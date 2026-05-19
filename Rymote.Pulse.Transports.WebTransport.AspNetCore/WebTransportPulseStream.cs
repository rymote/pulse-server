using System.Buffers.Binary;
using Rymote.Pulse.Core.Transport;

namespace Rymote.Pulse.Transports.WebTransport.AspNetCore;

/// <summary>
/// Adapts a single WebTransport stream (bidi or uni) to <see cref="IPulseStream"/>.
/// The on-stream wire format is [u32 BE length][envelope frame] per envelope.
/// </summary>
internal sealed class WebTransportPulseStream : IPulseStream
{
    public long StreamId { get; }
    public PulseStreamDirection Direction { get; }
    public bool IsClosed { get; private set; }

    private readonly Stream _readStream;
    private readonly Stream _writeStream;
    private readonly int _maxEnvelopeSizeInBytes;
    private readonly Action _abortAction;
    private bool _writeCompleted;
    private int _disposed;

    public WebTransportPulseStream(
        long streamId,
        PulseStreamDirection direction,
        Stream readStream,
        Stream writeStream,
        int maxEnvelopeSizeInBytes,
        Action abortAction)
    {
        StreamId = streamId;
        Direction = direction;
        _readStream = readStream;
        _writeStream = writeStream;
        _maxEnvelopeSizeInBytes = maxEnvelopeSizeInBytes;
        _abortAction = abortAction;
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
                chunk = await _readStream.ReadAsync(
                    buffer.AsMemory(bytesRead, buffer.Length - bytesRead),
                    cancellationToken).ConfigureAwait(false);
            }
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

        await _writeStream.WriteAsync(lengthPrefix.AsMemory(), cancellationToken).ConfigureAwait(false);
        await _writeStream.WriteAsync(envelopeFrame, cancellationToken).ConfigureAwait(false);
        await _writeStream.FlushAsync(cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask CompleteWritesAsync(CancellationToken cancellationToken)
    {
        if (_writeCompleted) return;
        _writeCompleted = true;
        try
        {
            await _writeStream.FlushAsync(cancellationToken).ConfigureAwait(false);
            await _writeStream.DisposeAsync().ConfigureAwait(false);
        }
        catch
        {
            // best-effort
        }
    }

    public ValueTask AbortAsync(int reasonCode, CancellationToken cancellationToken)
    {
        IsClosed = true;
        try { _abortAction(); }
        catch { /* best-effort */ }
        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        IsClosed = true;

        try { await _readStream.DisposeAsync().ConfigureAwait(false); } catch { /* ignore */ }
        if (!ReferenceEquals(_readStream, _writeStream))
        {
            try { await _writeStream.DisposeAsync().ConfigureAwait(false); } catch { /* ignore */ }
        }

        GC.SuppressFinalize(this);
    }
}
