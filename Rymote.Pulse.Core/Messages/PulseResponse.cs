namespace Rymote.Pulse.Core.Messages;

public class PulseResponse : PulseMessage
{
    public string Id { get; set; } = string.Empty;

    public string Response { get; set; } = string.Empty;

    public string Data { get; set; }

    public PulseStatus Status { get; set; } = PulseStatus.OK;

    public string Error { get; set; } = string.Empty;

    public PulseKind Kind { get; set; } = PulseKind.RPC;

    public bool IsStreamChunk { get; set; }

    public bool EndOfStream { get; set; }
    
    public string? ClientCorrelationId { get; set; }
}