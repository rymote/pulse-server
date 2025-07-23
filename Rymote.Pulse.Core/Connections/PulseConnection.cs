using System.Collections.Concurrent;
using System.Net.WebSockets;

namespace Rymote.Pulse.Core.Connections;

public class PulseConnection
{
    private const int MAX_METADATA_ENTRIES = 100;
    
    public string ConnectionId { get; }
    public WebSocket Socket { get; }
    public string NodeId { get; }

    private readonly ConcurrentDictionary<string, object> _metadata 
        = new ConcurrentDictionary<string, object>();
    public IReadOnlyDictionary<string, object> Metadata => _metadata;
    
    private readonly IReadOnlyDictionary<string, string> _queryParameters;
    public IReadOnlyDictionary<string, string> QueryParameters => _queryParameters;
    
    public PulseConnection(string connectionId, WebSocket socket, string nodeId, IDictionary<string, string>? queryParameters = null)
    {
        ConnectionId = connectionId;
        Socket = socket;
        NodeId = nodeId;
        
        _queryParameters = queryParameters != null 
            ? new Dictionary<string, string>(queryParameters) 
            : new Dictionary<string, string>();
    }

    public bool IsOpen => Socket.State == WebSocketState.Open;

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
        if (_metadata.Count >= MAX_METADATA_ENTRIES && !_metadata.ContainsKey(key))
            throw new InvalidOperationException($"Maximum metadata entries ({MAX_METADATA_ENTRIES}) exceeded");
        
        _metadata[key] = value;
    }

    public bool TryGetMetadata<TMetadataValue>(string key, out TMetadataValue? value)
    {
        if (_metadata.TryGetValue(key, out object? obj) && obj is TMetadataValue cast)
        {
            value = cast;
            return true;
        }
        
        value = default;
        return false;
    }

    public bool RemoveMetadata(string key) => _metadata.TryRemove(key, out _);
    
    
    public string? GetQueryParameter(string key)
    {
        return _queryParameters.TryGetValue(key, out string? value) ? value : null;
    }
    
    public bool TryGetQueryParameter(string key, out string? value)
    {
        return _queryParameters.TryGetValue(key, out value);
    }
}