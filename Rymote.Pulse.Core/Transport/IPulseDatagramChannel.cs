namespace Rymote.Pulse.Core.Transport;

public interface IPulseDatagramChannel
{
    int MaxDatagramEnvelopeSizeInBytes { get; }

    ValueTask<ReadOnlyMemory<byte>?> ReceiveDatagramAsync(CancellationToken cancellationToken);
    ValueTask SendDatagramAsync(ReadOnlyMemory<byte> envelopeFrame, CancellationToken cancellationToken);
}
