using MessagePack;

namespace Rymote.Pulse.Core.Messages;

[MessagePackObject]
public class PulseEnvelope<T>
{
    [Key(0)]
    public string? Id { get; set; } = Guid.NewGuid().ToString();

    [Key(1)]
    public string Handle { get; set; } = string.Empty;

    [Key(2)]
    public T Body { get; set; } = default!;

    [Key(3)]
    public string? AuthToken { get; set; } = string.Empty;

    [Key(4)]
    public PulseKind Kind { get; set; } = PulseKind.RPC;

    [Key(5)]
    public string Version { get; set; } = "v1";

    [Key(6)]
    public string? ClientCorrelationId { get; set; }

    [Key(7)]
    public PulseStatus? Status { get; set; }

    [Key(8)]
    public string? Error { get; set; }
}