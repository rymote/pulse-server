using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net.WebSockets;
using System.Threading.Tasks;
using Rymote.Pulse.Core.Cluster;

namespace Rymote.Pulse.Core;

public class PulseConnectionManager
{
    private readonly ConcurrentDictionary<string, WebSocket> _sockets = new();
    private readonly IClusterStore? _clusterStore;
    private readonly string _nodeId;

    public PulseConnectionManager(IClusterStore? clusterStore = null, string nodeId = "node1")
    {
        _clusterStore = clusterStore;
        _nodeId = nodeId;
    }

    public async Task AddConnectionAsync(string sessionId, WebSocket socket)
    {
        _sockets[sessionId] = socket;
        
        if (_clusterStore != null)
            await _clusterStore.AddConnectionAsync(sessionId, _nodeId);
    }

    public async Task RemoveConnectionAsync(string sessionId)
    {
        _sockets.TryRemove(sessionId, out _);
        
        if (_clusterStore != null)
            await _clusterStore.RemoveConnectionAsync(sessionId);
    }

    public WebSocket? GetSocket(string sessionId)
    {
        _sockets.TryGetValue(sessionId, out WebSocket? socket);
        return socket;
    }

    public IEnumerable<string> AllSessionIds => _sockets.Keys;
}