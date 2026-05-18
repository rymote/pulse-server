namespace Rymote.Pulse.Core.Transport;

public interface IPulseTransport
{
    string Name { get; }
    IAsyncEnumerable<IPulseTransportConnection> AcceptConnectionsAsync(CancellationToken cancellationToken);
}
