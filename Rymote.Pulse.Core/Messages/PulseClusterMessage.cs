using MessagePack;

namespace Rymote.Pulse.Core.Messages;

[MessagePackObject]
public class PulseClusterMessage
{
    [Key(0)]
    public PulseClusterMessageType Type { get; set; }
    
    [Key(1)]
    public string? GroupName { get; set; }
    
    [Key(2)]
    public string? TargetConnectionId { get; set; }
    
    [Key(3)]
    public byte[]? Payload { get; set; }
    
    [Key(4)]
    public string SourceNodeId { get; set; } = string.Empty;
    
    [Key(5)]
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
}