namespace Rymote.Pulse.Transports.WebTransport.AspNetCore;

public class PulseWebTransportOptions
{
    public int MaxStreamEnvelopeSizeInBytes { get; set; } = 10 * 1024 * 1024;
    public int MaxDatagramEnvelopeSizeInBytes { get; set; } = 1200;
    public bool DatagramsEnabled { get; set; } = true;
}
