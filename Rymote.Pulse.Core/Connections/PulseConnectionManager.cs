using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net.WebSockets;
using System.Threading.Tasks;
using Rymote.Pulse.Core.Cluster;
using Rymote.Pulse.Core.Connections;

namespace Rymote.Pulse.Core.Connections;

public class PulseConnectionManager
{
    private readonly ConcurrentDictionary<string, PulseConnection> _connections = new();
    private readonly ConcurrentDictionary<string, PulseGroup> _groups = new();
    
    private readonly IClusterStore? _clusterStore;
    private readonly string _nodeId;

    public PulseConnectionManager(IClusterStore? clusterStore = null, string? nodeId = null)
    {
        _clusterStore = clusterStore;
        _nodeId = nodeId ?? Environment.MachineName;
    }

    public async Task<PulseConnection> AddConnectionAsync(string connectionId, WebSocket socket)
    {
        PulseConnection connection = new PulseConnection(connectionId, socket, _nodeId);
        _connections[connectionId] = connection;

        if (_clusterStore != null)
            await _clusterStore.AddConnectionAsync(connectionId, _nodeId);

        return connection;
    }

    public async Task RemoveConnectionAsync(string connectionId)
    {
        _connections.TryRemove(connectionId, out _);

        foreach (PulseGroup group in _groups.Values)
            group.Remove(connectionId);

        if (_clusterStore != null)
            await _clusterStore.RemoveConnectionAsync(connectionId);
    }

    public PulseConnection? GetConnection(string connectionId)
        => _connections.TryGetValue(connectionId, out PulseConnection? connection) ? connection : null;

    public PulseGroup GetOrCreateGroup(string groupName)
        => _groups.GetOrAdd(groupName, _ => new PulseGroup(groupName));
    
    
    public async Task AddToGroupAsync(string groupName, string connectionId)
    {
        if (_connections.TryGetValue(connectionId, out var conn))
            await AddToGroupAsync(groupName, conn);
    }

    public async Task AddToGroupAsync(string groupName, PulseConnection connection)
    {
        var group = GetOrCreateGroup(groupName);
        group.Add(connection);

        if (_clusterStore != null)
            await _clusterStore.AddToGroupAsync(groupName, connection.ConnectionId, _nodeId);
    }

    public async Task RemoveFromGroupAsync(string groupName, string connectionId)
    {
        if (_connections.TryGetValue(connectionId, out PulseConnection? connection))
            await RemoveFromGroupAsync(groupName, connection);
        else if (_groups.TryGetValue(groupName, out PulseGroup? group))
            group.Remove(connectionId);

        if (_clusterStore != null)
            await _clusterStore.RemoveFromGroupAsync(groupName, connectionId);
    }

    public async Task RemoveFromGroupAsync(string groupName, PulseConnection connection)
    {
        if (_groups.TryGetValue(groupName, out var group))
            group.Remove(connection.ConnectionId);

        if (_clusterStore != null)
            await _clusterStore.RemoveFromGroupAsync(groupName, connection.ConnectionId);
    }
    
    public async Task<IEnumerable<string>> GetAllConnectionIdsAsync()
    {
        if (_clusterStore != null)
        {
            var map = await _clusterStore.GetAllConnectionsAsync();
            return map.Keys;
        }
        
        return _connections.Keys;
    }
}