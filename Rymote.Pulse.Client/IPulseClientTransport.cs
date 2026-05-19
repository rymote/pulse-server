using Rymote.Pulse.Core.Transport;

namespace Rymote.Pulse.Client;

public interface IPulseClientTransport
{
    string Name { get; }

    Task<IPulseSession> ConnectAsync(
        Uri endpoint,
        string? authToken,
        IReadOnlyDictionary<string, string>? queryParameters,
        CancellationToken cancellationToken);
}
