using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Net.WebSockets;
using System.Threading.Tasks;
using Rymote.Pulse.Core.Cluster;
using Rymote.Pulse.Core.Connections;
using Rymote.Pulse.Core.Logging;

namespace Rymote.Pulse.Core.Connections;

public class PulseConnectionManager
{
    private readonly ConcurrentDictionary<string, PulseConnection> _connections = new();
    private readonly ConcurrentDictionary<string, PulseGroup> _groups = new();
    
    private readonly IPulseClusterStore? _clusterStore;
    private readonly string _nodeId;
    private readonly IPulseLogger? _logger;

    public PulseConnectionManager(IPulseClusterStore? clusterStore = null, string? nodeId = null, IPulseLogger? logger = null)
    {
        _clusterStore = clusterStore;
        _nodeId = nodeId ?? Environment.MachineName;
        _logger = logger;
    }

    public async Task<PulseConnection> AddConnectionAsync(string connectionId, WebSocket socket, IDictionary<string, string>? queryParameters = null)
    {
        PulseConnection connection = new PulseConnection(connectionId, socket, _nodeId, queryParameters);
        _connections[connectionId] = connection;

        if (_clusterStore != null)
            await _clusterStore.AddConnectionAsync(connectionId, _nodeId);

        return connection;
    }

    public async Task RemoveConnectionAsync(string connectionId)
    {
        if (_connections.TryRemove(connectionId, out PulseConnection? connection))
            connection.Dispose();

        List<string> groupsToCheck = [];
        
        foreach (KeyValuePair<string, PulseGroup> keyValuePair in _groups)
        {
            keyValuePair.Value.Remove(connectionId);
            if (!keyValuePair.Value.Members.Any())
                groupsToCheck.Add(keyValuePair.Key);
        }
        
        foreach (string groupName in groupsToCheck)
            if (_groups.TryRemove(groupName, out PulseGroup? group) && !group.Members.Any())
                _logger?.LogInfo($"Removed empty group: {groupName}");

        if (_clusterStore != null)
            await _clusterStore.RemoveConnectionAsync(connectionId);
    }

    public PulseConnection? GetConnection(string connectionId)
        => _connections.GetValueOrDefault(connectionId);

    public PulseGroup GetOrCreateGroup(string groupName)
        => _groups.GetOrAdd(groupName, _ => new PulseGroup(groupName));
    
    public async Task DisconnectAsync(
        string connectionId, 
        WebSocketCloseStatus closeStatus = WebSocketCloseStatus.NormalClosure,
        string? statusDescription = null,
        CancellationToken cancellationToken = default)
    {
        PulseConnection? connection = GetConnection(connectionId);
        
        if (connection != null)
            await DisconnectAsync(connection, closeStatus, statusDescription, cancellationToken);
    }
    
    public async Task DisconnectAsync(
        PulseConnection connection,
        WebSocketCloseStatus closeStatus = WebSocketCloseStatus.NormalClosure,
        string? statusDescription = null,
        CancellationToken cancellationToken = default)
    {
        try
        {
            await connection.DisconnectAsync(closeStatus, statusDescription, cancellationToken);
        }
        catch (Exception exception)
        {
            _logger?.LogError($"Error disconnecting connection {connection.ConnectionId}", exception);
        }
        finally
        {
            await RemoveConnectionAsync(connection.ConnectionId);
        }
    }
    
    public async Task AddToGroupAsync(string groupName, string connectionId)
    {
        if (_connections.TryGetValue(connectionId, out PulseConnection? connection))
            await AddToGroupAsync(groupName, connection);
    }

    public async Task AddToGroupAsync(string groupName, PulseConnection connection)
    {
        PulseGroup group = GetOrCreateGroup(groupName);
        group.Add(connection);

        if (_clusterStore != null)
            await _clusterStore.AddToGroupAsync(groupName, connection.ConnectionId, _nodeId);
    }

    public async Task RemoveFromGroupAsync(string groupName, string connectionId)
    {
        if (_connections.TryGetValue(connectionId, out PulseConnection? connection))
        {
            await RemoveFromGroupAsync(groupName, connection);
        }
        else if (_groups.TryGetValue(groupName, out PulseGroup? group))
        {
            group.Remove(connectionId);
            
            if (!group.Members.Any() && _groups.TryRemove(groupName, out _))
                _logger?.LogInfo($"Removed empty group: {groupName}");
        }

        if (_clusterStore != null)
            await _clusterStore.RemoveFromGroupAsync(groupName, connectionId);
    }

    public async Task RemoveFromGroupAsync(string groupName, PulseConnection connection)
    {
        if (_groups.TryGetValue(groupName, out PulseGroup? group))
        {
            group.Remove(connection.ConnectionId);
            
            if (!group.Members.Any() && _groups.TryRemove(groupName, out _))
                _logger?.LogInfo($"Removed empty group: {groupName}");
        }

        if (_clusterStore != null)
            await _clusterStore.RemoveFromGroupAsync(groupName, connection.ConnectionId);
    }
    
    public async Task<IEnumerable<string>> GetAllConnectionIdsAsync()
    {
        if (_clusterStore == null) return _connections.Keys;
        Dictionary<string, string> map = await _clusterStore.GetAllConnectionsAsync();
        return map.Keys;
    }
}