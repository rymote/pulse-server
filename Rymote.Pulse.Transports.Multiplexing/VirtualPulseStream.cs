using System.Threading.Channels;
using Rymote.Pulse.Core.Transport;

namespace Rymote.Pulse.Transports.Multiplexing;

internal sealed class VirtualPulseStream : IPulseStream
{
    public long StreamId { get; }
    public PulseStreamDirection Direction { get; }
    public bool IsClosed { get; private set; }

    private readonly PulseStreamMultiplexer _multiplexer;
    private readonly Channel<ReadOnlyMemory<byte>> _incomingEnvelopes;
    private bool _writeCompleted;
    private int _disposed;

    public VirtualPulseStream(PulseStreamMultiplexer multiplexer, long streamId, PulseStreamDirection direction)
    {
        _multiplexer = multiplexer;
        StreamId = streamId;
        Direction = direction;
        _incomingEnvelopes = Channel.CreateUnbounded<ReadOnlyMemory<byte>>();
    }

    public async ValueTask<ReadOnlyMemory<byte>?> ReadEnvelopeAsync(CancellationToken cancellationToken)
    {
        if (Direction == PulseStreamDirection.UnidirectionalClientToServer && !_multiplexer.IsServerSide)
            throw new InvalidOperationException("Client cannot read from a client-to-server uni stream.");
        if (Direction == PulseStreamDirection.UnidirectionalServerToClient && _multiplexer.IsServerSide)
            throw new InvalidOperationException("Server cannot read from a server-to-client uni stream.");

        try
        {
            if (await _incomingEnvelopes.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
                return await _incomingEnvelopes.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (ChannelClosedException channelClosedException)
            when (channelClosedException.InnerException is PulseStreamResetException resetException)
        {
            throw resetException;
        }
        catch (ChannelClosedException)
        {
            // graceful FIN
        }

        return null;
    }

    public ValueTask WriteEnvelopeAsync(ReadOnlyMemory<byte> envelopeFrame, CancellationToken cancellationToken)
    {
        if (Direction == PulseStreamDirection.UnidirectionalClientToServer && _multiplexer.IsServerSide)
            throw new InvalidOperationException("Server cannot write to a client-to-server uni stream.");
        if (Direction == PulseStreamDirection.UnidirectionalServerToClient && !_multiplexer.IsServerSide)
            throw new InvalidOperationException("Client cannot write to a server-to-client uni stream.");
        if (_writeCompleted)
            throw new InvalidOperationException("Stream write side already completed.");
        if (IsClosed)
            throw new InvalidOperationException("Stream is closed.");

        return _multiplexer.QueueOutboundFrameAsync(
            new MultiplexerOutboundFrame(StreamId, MultiplexerFrameOps.DATA, envelopeFrame),
            cancellationToken);
    }

    public ValueTask CompleteWritesAsync(CancellationToken cancellationToken)
    {
        if (_writeCompleted) return ValueTask.CompletedTask;
        _writeCompleted = true;

        return _multiplexer.QueueOutboundFrameAsync(
            new MultiplexerOutboundFrame(StreamId, MultiplexerFrameOps.FIN, ReadOnlyMemory<byte>.Empty),
            cancellationToken);
    }

    public ValueTask AbortAsync(int reasonCode, CancellationToken cancellationToken)
    {
        IsClosed = true;
        _incomingEnvelopes.Writer.TryComplete(new PulseStreamResetException(reasonCode));
        return _multiplexer.QueueOutboundFrameAsync(
            MultiplexerOutboundFrame.Reset(StreamId, reasonCode),
            cancellationToken);
    }

    internal void OnFinFromPeer()
    {
        _incomingEnvelopes.Writer.TryComplete();
    }

    internal void OnResetFromPeer(int reasonCode)
    {
        IsClosed = true;
        _incomingEnvelopes.Writer.TryComplete(new PulseStreamResetException(reasonCode));
    }

    internal bool TryEnqueueIncomingEnvelope(ReadOnlyMemory<byte> envelopeFrame)
    {
        return _incomingEnvelopes.Writer.TryWrite(envelopeFrame);
    }

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return ValueTask.CompletedTask;
        IsClosed = true;
        _incomingEnvelopes.Writer.TryComplete();
        _multiplexer.OnStreamDisposed(StreamId);
        GC.SuppressFinalize(this);
        return ValueTask.CompletedTask;
    }
}
