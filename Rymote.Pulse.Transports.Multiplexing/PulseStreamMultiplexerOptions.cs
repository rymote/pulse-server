namespace Rymote.Pulse.Transports.Multiplexing;

public class PulseStreamMultiplexerOptions
{
    public int MaxFramePayloadSizeInBytes { get; set; } = 10 * 1024 * 1024;
    public int MaxDatagramEnvelopeSizeInBytes { get; set; } = 1200;
    public bool DatagramsEnabled { get; set; } = true;
}
