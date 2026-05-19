using System.Net;

namespace Rymote.Pulse.Transports.WebSockets.HttpListener;

public class WebSocketHttpListenerTransportOptions
{
    public IList<string> Prefixes { get; } = new List<string>();
    public int InitialReceiveBufferSizeInBytes { get; set; } = 4 * 1024;
    public int MaxFramePayloadSizeInBytes { get; set; } = 10 * 1024 * 1024;
    public int MaxDatagramEnvelopeSizeInBytes { get; set; } = 1200;
    public bool DatagramsEnabled { get; set; } = true;
    public int? MaxConcurrentConnections { get; set; }
    public AuthenticationSchemes AuthenticationSchemes { get; set; } = AuthenticationSchemes.Anonymous;
    public TimeSpan ShutdownDrainTimeout { get; set; } = TimeSpan.FromSeconds(10);

    internal void Validate()
    {
        if (Prefixes.Count == 0)
            throw new InvalidOperationException(
                $"{nameof(WebSocketHttpListenerTransportOptions)}.{nameof(Prefixes)} must contain at least one HTTP prefix (e.g. \"http://+:8080/pulse/\").");

        if (InitialReceiveBufferSizeInBytes <= 0)
            throw new InvalidOperationException(
                $"{nameof(WebSocketHttpListenerTransportOptions)}.{nameof(InitialReceiveBufferSizeInBytes)} must be greater than zero.");

        if (MaxFramePayloadSizeInBytes <= 0)
            throw new InvalidOperationException(
                $"{nameof(WebSocketHttpListenerTransportOptions)}.{nameof(MaxFramePayloadSizeInBytes)} must be greater than zero.");

        if (MaxConcurrentConnections is <= 0)
            throw new InvalidOperationException(
                $"{nameof(WebSocketHttpListenerTransportOptions)}.{nameof(MaxConcurrentConnections)} must be greater than zero when set.");

        if (ShutdownDrainTimeout < TimeSpan.Zero)
            throw new InvalidOperationException(
                $"{nameof(WebSocketHttpListenerTransportOptions)}.{nameof(ShutdownDrainTimeout)} cannot be negative.");
    }
}
