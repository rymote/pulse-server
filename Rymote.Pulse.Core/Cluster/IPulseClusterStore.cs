namespace Rymote.Pulse.Core.Cluster;

public interface IPulseClusterStore
{
    Task AddConnectionAsync(string connectionId, string nodeId);

    Task RemoveConnectionAsync(string connectionId);

    Task AddToGroupAsync(string groupName, string connectionId, string nodeId);

    Task RemoveFromGroupAsync(string groupName, string connectionId);

    Task<Dictionary<string, string>> GetAllConnectionsAsync();

    Task<Dictionary<string, string>> GetGroupMembersAsync(string groupName);
    
    Task<HashSet<string>> GetGroupNodesAsync(string groupName);
}