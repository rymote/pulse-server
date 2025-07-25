namespace Rymote.Pulse.Core.Cluster;

public interface IPulseClusterMessaging
{
    Task SendToNodeAsync(string nodeId, byte[] message, CancellationToken cancellationToken = default);
    
    Task BroadcastToClusterAsync(byte[] message, CancellationToken cancellationToken = default);
    
    Task SubscribeToClusterMessagesAsync(Func<string, byte[], Task> messageHandler, CancellationToken cancellationToken = default);
    
    string CurrentNodeId { get; }
}