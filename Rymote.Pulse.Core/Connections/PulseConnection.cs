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

    public bool IsOpen => _session.IsOpen;
    public string TransportName => _session.TransportName;

    internal IPulseSession Session => _session;
    private readonly IPulseSession _session;

    private bool _disposed;

    public PulseConnection(IPulseSession session, string nodeId)
    {
        ArgumentNullException.ThrowIfNull(session);

        _session = session;
        ConnectionId = session.SessionId;
        QueryParameters = session.QueryParameters;
        NodeId = nodeId;
        Metadata = new PulseMetadata();
    }

    public async Task SendAsync(
        ReadOnlyMemory<byte> envelopeFrame,
        PulseDeliveryMode deliveryMode = PulseDeliveryMode.Reliable,
        CancellationToken cancellationToken = default)
    {
        if (deliveryMode == PulseDeliveryMode.Datagram)
        {
            if (_session.Datagrams == null)
                throw new NotSupportedException(
                    $"Transport '{_session.TransportName}' does not support datagrams.");

            await _session.Datagrams.SendDatagramAsync(envelopeFrame, cancellationToken).ConfigureAwait(false);
            return;
        }

        IPulseStream stream = await _session.OpenStreamAsync(
            PulseStreamDirection.UnidirectionalServerToClient,
            cancellationToken).ConfigureAwait(false);

        try
        {
            await stream.WriteEnvelopeAsync(envelopeFrame, cancellationToken).ConfigureAwait(false);
            await stream.CompleteWritesAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            await stream.DisposeAsync().ConfigureAwait(false);
        }
    }

    public async Task SendEventAsync<TPayload>(
        string handle,
        TPayload data,
        string version = "v1",
        PulseDeliveryMode deliveryMode = PulseDeliveryMode.Reliable,
        CancellationToken cancellationToken = default)
    {
        PulseEnvelope<TPayload> envelope = new PulseEnvelope<TPayload>
        {
            Handle = handle,
            Body = data,
            Kind = deliveryMode == PulseDeliveryMode.Datagram ? PulseKind.DATAGRAM_EVENT : PulseKind.EVENT,
            Version = version
        };

        byte[] envelopeBytes = MsgPackSerdes.Serialize(envelope);
        await SendAsync(envelopeBytes, deliveryMode, cancellationToken).ConfigureAwait(false);
    }

    public async Task SendEventAsync(
        string handle,
        object data,
        string version = "v1",
        PulseDeliveryMode deliveryMode = PulseDeliveryMode.Reliable,
        CancellationToken cancellationToken = default)
    {
        PulseEnvelope<object> envelope = new PulseEnvelope<object>
        {
            Handle = handle,
            Body = data,
            Kind = deliveryMode == PulseDeliveryMode.Datagram ? PulseKind.DATAGRAM_EVENT : PulseKind.EVENT,
            Version = version
        };

        byte[] envelopeBytes = MsgPackSerdes.Serialize(envelope);
        await SendAsync(envelopeBytes, deliveryMode, cancellationToken).ConfigureAwait(false);
    }

    internal ValueTask DisconnectAsync(
        int reasonCode = 1000,
        string? reason = null,
        CancellationToken cancellationToken = default)
        => _session.CloseAsync(reasonCode, TimeSpan.Zero, cancellationToken);

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
        await _session.DisposeAsync().ConfigureAwait(false);
        GC.SuppressFinalize(this);
    }

    public void Dispose()
    {
        DisposeAsync().AsTask().GetAwaiter().GetResult();
    }
}
