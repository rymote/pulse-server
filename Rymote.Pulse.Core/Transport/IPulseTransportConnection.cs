namespace Rymote.Pulse.Core.Transport;

public interface IPulseTransportConnection : IAsyncDisposable
{
    string ConnectionId { get; }
    string TransportName { get; }
    bool IsOpen { get; }
    IReadOnlyDictionary<string, string> QueryParameters { get; }
    IReadOnlyDictionary<string, object> InitialMetadata { get; }

    ValueTask<ReadOnlyMemory<byte>?> ReceiveMessageAsync(CancellationToken cancellationToken);
    ValueTask SendMessageAsync(ReadOnlyMemory<byte> payload, CancellationToken cancellationToken);
    ValueTask CloseAsync(int closeCode, string? reason, CancellationToken cancellationToken);
}
