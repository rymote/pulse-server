namespace Rymote.Pulse.Core.Transport;

public interface IPulseSession : IAsyncDisposable
{
    string SessionId { get; }
    string TransportName { get; }
    bool IsOpen { get; }
    IReadOnlyDictionary<string, string> QueryParameters { get; }
    IReadOnlyDictionary<string, object> InitialMetadata { get; }
    IPulseDatagramChannel? Datagrams { get; }

    ValueTask<IPulseStream?> AcceptStreamAsync(CancellationToken cancellationToken);
    ValueTask<IPulseStream> OpenStreamAsync(PulseStreamDirection direction, CancellationToken cancellationToken);
    ValueTask CloseAsync(int reasonCode, TimeSpan drainTimeout, CancellationToken cancellationToken);
}
