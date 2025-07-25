using System.Collections.Concurrent;
using System.Net.WebSockets;
using Rymote.Pulse.Core.Messages;
using Rymote.Pulse.Core.Metadata;
using Rymote.Pulse.Core.Serialization;

namespace Rymote.Pulse.Core.Connections;

public class PulseConnection : IDisposable
{
    public string ConnectionId { get; }
    public WebSocket Socket { get; }
    public string NodeId { get; }
    public PulseMetadata Metadata { get; }
    private readonly IReadOnlyDictionary<string, string> _queryParameters;
    public IReadOnlyDictionary<string, string> QueryParameters => _queryParameters;
    private bool _disposed;
    
    public PulseConnection(string connectionId, WebSocket socket, string nodeId, IDictionary<string, string>? queryParameters = null)
    {
        ConnectionId = connectionId;
        Socket = socket;
        NodeId = nodeId;
        Metadata = new PulseMetadata();
        
        _queryParameters = queryParameters != null 
            ? new Dictionary<string, string>(queryParameters) 
            : new Dictionary<string, string>();
    }

    public bool IsOpen => Socket.State == WebSocketState.Open;

    
    public async Task SendEventAsync<TPayload>(
        string handle,
        TPayload data,
        string version = "v1",
        CancellationToken cancellationToken = default
    ) where TPayload : class, new()
    {
        PulseEnvelope<TPayload> envelope = new PulseEnvelope<TPayload>
        {
            Handle = handle,
            Body = data,
            Kind = PulseKind.EVENT,
            Version = version
        };

        byte[] envelopeBytes = MsgPackSerdes.Serialize(envelope);
        await SendAsync(envelopeBytes, cancellationToken);
    }

    public async Task SendEventAsync(
        string handle,
        object data,
        string version = "v1",
        CancellationToken cancellationToken = default
    )
    {
        PulseEnvelope<object> envelope = new PulseEnvelope<object>
        {
            Handle = handle,
            Body = data,
            Kind = PulseKind.EVENT,
            Version = version
        };

        byte[] envelopeBytes = MsgPackSerdes.Serialize(envelope);
        await SendAsync(envelopeBytes, cancellationToken);
    }
    
    public Task SendAsync(byte[] payload, CancellationToken cancellationToken = default)
        => Socket.SendAsync(
            new ArraySegment<byte>(payload),
            WebSocketMessageType.Binary,
            endOfMessage: true,
            cancellationToken: cancellationToken
        );
    
    internal async Task DisconnectAsync(
        WebSocketCloseStatus closeStatus = WebSocketCloseStatus.NormalClosure, 
        string? statusDescription = null,
        CancellationToken cancellationToken = default)
    {
        if (Socket.State == WebSocketState.Open)
            await Socket.CloseAsync(
                closeStatus, 
                statusDescription ?? "Connection closed by server", 
                cancellationToken);
    }
    
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
        return _queryParameters.GetValueOrDefault(key);
    }
    
    public bool TryGetQueryParameter(string key, out string? value)
    {
        return _queryParameters.TryGetValue(key, out value);
    }
    
    
    public void Dispose()
    {
        if (_disposed) return;
        
        _disposed = true;
        Metadata?.Dispose();
        GC.SuppressFinalize(this);
    }
}