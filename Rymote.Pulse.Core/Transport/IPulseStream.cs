namespace Rymote.Pulse.Core.Transport;

public interface IPulseStream : IAsyncDisposable
{
    long StreamId { get; }
    PulseStreamDirection Direction { get; }
    bool IsClosed { get; }

    ValueTask<ReadOnlyMemory<byte>?> ReadEnvelopeAsync(CancellationToken cancellationToken);
    ValueTask WriteEnvelopeAsync(ReadOnlyMemory<byte> envelopeFrame, CancellationToken cancellationToken);
    ValueTask CompleteWritesAsync(CancellationToken cancellationToken);
    ValueTask AbortAsync(int reasonCode, CancellationToken cancellationToken);
}
