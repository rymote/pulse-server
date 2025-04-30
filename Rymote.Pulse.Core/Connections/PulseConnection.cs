using System.Net.WebSockets;

namespace Rymote.Pulse.Core.Connections;

public class PulseConnection
{
    public string ConnectionId { get; }
    public WebSocket Socket { get; }
    public string NodeId { get; }

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
}