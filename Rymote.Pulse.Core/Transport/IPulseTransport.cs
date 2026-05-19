namespace Rymote.Pulse.Core.Transport;

public interface IPulseTransport
{
    string Name { get; }
    IAsyncEnumerable<IPulseSession> AcceptSessionsAsync(CancellationToken cancellationToken);
}
