using Rymote.Pulse.Core.Transport;

namespace Rymote.Pulse.Transports.WebTransport.AspNetCore;

/// <summary>
/// Adapts WebTransport datagram I/O to <see cref="IPulseDatagramChannel"/>.
/// </summary>
internal sealed class WebTransportPulseDatagramChannel : IPulseDatagramChannel
{
    public int MaxDatagramEnvelopeSizeInBytes { get; }

    private readonly Func<CancellationToken, ValueTask<ReadOnlyMemory<byte>?>> _receiveDatagramFunc;
    private readonly Func<ReadOnlyMemory<byte>, CancellationToken, ValueTask> _sendDatagramFunc;

    public WebTransportPulseDatagramChannel(
        int maxDatagramEnvelopeSizeInBytes,
        Func<CancellationToken, ValueTask<ReadOnlyMemory<byte>?>> receiveDatagramFunc,
        Func<ReadOnlyMemory<byte>, CancellationToken, ValueTask> sendDatagramFunc)
    {
        MaxDatagramEnvelopeSizeInBytes = maxDatagramEnvelopeSizeInBytes;
        _receiveDatagramFunc = receiveDatagramFunc;
        _sendDatagramFunc = sendDatagramFunc;
    }

    public ValueTask<ReadOnlyMemory<byte>?> ReceiveDatagramAsync(CancellationToken cancellationToken)
        => _receiveDatagramFunc(cancellationToken);

    public ValueTask SendDatagramAsync(ReadOnlyMemory<byte> envelopeFrame, CancellationToken cancellationToken)
    {
        if (envelopeFrame.Length > MaxDatagramEnvelopeSizeInBytes)
            throw new InvalidOperationException(
                $"Datagram envelope ({envelopeFrame.Length} bytes) exceeds MaxDatagramEnvelopeSizeInBytes ({MaxDatagramEnvelopeSizeInBytes}).");

        return _sendDatagramFunc(envelopeFrame, cancellationToken);
    }
}
