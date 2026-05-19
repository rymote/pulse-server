namespace Rymote.Pulse.Client.Transports.WebSockets;

public class WebSocketClientTransportOptions
{
    public int InitialReceiveBufferSizeInBytes { get; set; } = 4 * 1024;
    public int MaxFramePayloadSizeInBytes { get; set; } = 10 * 1024 * 1024;
    public int MaxDatagramEnvelopeSizeInBytes { get; set; } = 1200;
    public bool DatagramsEnabled { get; set; } = true;
}
