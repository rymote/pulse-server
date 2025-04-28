namespace Rymote.Pulse.Core.Messages;

public class PulseRequest : PulseMessage
{
    public string Id { get; set; } = string.Empty;

    public string Request { get; set; } = string.Empty; 

    public string Payload { get; set; }

    public string AuthToken { get; set; } = string.Empty;

    public PulseKind Kind { get; set; } = PulseKind.RPC;

    public string? Version { get; set; } = "v1";
    
    public string? ClientCorrelationId { get; set; }
}