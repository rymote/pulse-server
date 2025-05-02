using System.Collections.Concurrent;
using System.Net.WebSockets;

namespace Rymote.Pulse.Core.Connections;

public class PulseConnection
{
    public string ConnectionId { get; }
    public WebSocket Socket { get; }
    public string NodeId { get; }

    private readonly ConcurrentDictionary<string, object> _metadata 
        = new ConcurrentDictionary<string, object>();

    public IReadOnlyDictionary<string, object> Metadata => _metadata;
    
    public PulseConnection(string connectionId, WebSocket socket, string nodeId)
    {
        ConnectionId = connectionId;
        Socket = socket;
        NodeId = nodeId;
    }

    public bool IsOpen => Socket.State == WebSocketState.Open;

    public Task SendAsync(byte[] payload, CancellationToken cancellationToken = default)
        => Socket.SendAsync(
            new ArraySegment<byte>(payload),
            WebSocketMessageType.Binary,
            endOfMessage: true,
            cancellationToken: cancellationToken
        );
    
    public void SetMetadata(string key, object value) => _metadata[key] = value;

    public bool TryGetMetadata<T>(string key, out T? value)
    {
        if (_metadata.TryGetValue(key, out object? obj) && obj is T cast)
        {
            value = cast;
            return true;
        }
        
        value = default;
        return false;
    }

    public bool RemoveMetadata(string key) => _metadata.TryRemove(key, out _);
}