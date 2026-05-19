using System.Threading.Channels;
using Rymote.Pulse.Core.Transport;

namespace Rymote.Pulse.Transports.Multiplexing;

internal sealed class MultiplexedDatagramChannel : IPulseDatagramChannel
{
    public int MaxDatagramEnvelopeSizeInBytes { get; }

    private readonly PulseStreamMultiplexer _multiplexer;
    private readonly Channel<ReadOnlyMemory<byte>> _incomingDatagrams;

    public MultiplexedDatagramChannel(PulseStreamMultiplexer multiplexer, int maxDatagramEnvelopeSizeInBytes)
    {
        _multiplexer = multiplexer;
        MaxDatagramEnvelopeSizeInBytes = maxDatagramEnvelopeSizeInBytes;
        _incomingDatagrams = Channel.CreateUnbounded<ReadOnlyMemory<byte>>();
    }

    public async ValueTask<ReadOnlyMemory<byte>?> ReceiveDatagramAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (await _incomingDatagrams.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
                return await _incomingDatagrams.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (ChannelClosedException)
        {
        }
        return null;
    }

    public ValueTask SendDatagramAsync(ReadOnlyMemory<byte> envelopeFrame, CancellationToken cancellationToken)
    {
        if (envelopeFrame.Length > MaxDatagramEnvelopeSizeInBytes)
            throw new InvalidOperationException(
                $"Datagram envelope ({envelopeFrame.Length} bytes) exceeds MaxDatagramEnvelopeSizeInBytes ({MaxDatagramEnvelopeSizeInBytes}).");

        return _multiplexer.QueueOutboundFrameAsync(
            new MultiplexerOutboundFrame(0, MultiplexerFrameOps.DATAGRAM, envelopeFrame),
            cancellationToken);
    }

    internal void EnqueueIncoming(ReadOnlyMemory<byte> envelopeFrame)
    {
        _incomingDatagrams.Writer.TryWrite(envelopeFrame);
    }

    internal void Complete()
    {
        _incomingDatagrams.Writer.TryComplete();
    }
}
