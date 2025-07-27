namespace Rymote.Pulse.AspNet;

public class PulseProtocolOptions
{
    public int BufferSizeInBytes { get; set; } = 4 * 1024;
    public int MaxMessageSizeInBytes { get; set; } = 10 * 1024 * 1024;
}