using Rymote.Pulse.Core.Messages;
using Rymote.Pulse.Core.Metadata;
using Rymote.Pulse.Core.Serialization;
using Rymote.Pulse.Core.Transport;

namespace Rymote.Pulse.Core.Connections;

public class PulseConnection : IAsyncDisposable, IDisposable
{
    public string ConnectionId { get; }
    public string NodeId { get; }
    public PulseMetadata Metadata { get; }
    public IReadOnlyDictionary<string, string> QueryParameters { get; }

    public bool IsOpen => _transportConnection.IsOpen;
    public string TransportName => _transportConnection.TransportName;

    internal IPulseTransportConnection TransportConnection => _transportConnection;
    private readonly IPulseTransportConnection _transportConnection;

    private bool _disposed;

    public PulseConnection(IPulseTransportConnection transportConnection, string nodeId)
    {
        ArgumentNullException.ThrowIfNull(transportConnection);

        _transportConnection = transportConnection;
        ConnectionId = transportConnection.ConnectionId;
        QueryParameters = transportConnection.QueryParameters;
        NodeId = nodeId;
        Metadata = new PulseMetadata();
    }

    public ValueTask SendAsync(ReadOnlyMemory<byte> payload, CancellationToken cancellationToken = default)
        => _transportConnection.SendMessageAsync(payload, cancellationToken);

    public async Task SendEventAsync<TPayload>(
        string handle,
        TPayload data,
        string version = "v1",
        CancellationToken cancellationToken = default)
    {
        PulseEnvelope<TPayload> envelope = new PulseEnvelope<TPayload>
        {
            Handle = handle,
            Body = data,
            Kind = PulseKind.EVENT,
            Version = version
        };

        byte[] envelopeBytes = MsgPackSerdes.Serialize(envelope);
        await SendAsync(envelopeBytes, cancellationToken).ConfigureAwait(false);
    }

    public async Task SendEventAsync(
        string handle,
        object data,
        string version = "v1",
        CancellationToken cancellationToken = default)
    {
        PulseEnvelope<object> envelope = new PulseEnvelope<object>
        {
            Handle = handle,
            Body = data,
            Kind = PulseKind.EVENT,
            Version = version
        };

        byte[] envelopeBytes = MsgPackSerdes.Serialize(envelope);
        await SendAsync(envelopeBytes, cancellationToken).ConfigureAwait(false);
    }

    internal ValueTask DisconnectAsync(
        int closeCode,
        string? reason = null,
        CancellationToken cancellationToken = default)
        => _transportConnection.CloseAsync(closeCode, reason, cancellationToken);

    public void SetMetadata(string key, object value)
    {
        Metadata.SetAsync(key, value).GetAwaiter().GetResult();
    }

    public bool TryGetMetadata<TMetadataValue>(string key, out TMetadataValue? value)
    {
        return Metadata.TryGet(key, out value);
    }

    public bool RemoveMetadata(string key)
    {
        return Metadata.RemoveAsync(key).GetAwaiter().GetResult();
    }

    public string? GetQueryParameter(string key)
    {
        return QueryParameters.GetValueOrDefault(key);
    }

    public bool TryGetQueryParameter(string key, out string? value)
    {
        return QueryParameters.TryGetValue(key, out value);
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        Metadata.Dispose();
        await _transportConnection.DisposeAsync().ConfigureAwait(false);
        GC.SuppressFinalize(this);
    }

    public void Dispose()
    {
        DisposeAsync().AsTask().GetAwaiter().GetResult();
    }
}
