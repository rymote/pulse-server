using Microsoft.Extensions.DependencyInjection;
using Rymote.Pulse.Hosting;

namespace Rymote.Pulse.Transports.WebSockets.HttpListener;

public static class WebSocketHttpListenerTransportServiceCollectionExtensions
{
    public static IPulseBuilder AddWebSocketHttpListenerTransport(
        this IPulseBuilder pulseBuilder,
        Action<WebSocketHttpListenerTransportOptions> configureOptionsAction)
    {
        ArgumentNullException.ThrowIfNull(pulseBuilder);
        ArgumentNullException.ThrowIfNull(configureOptionsAction);

        pulseBuilder.Services.Configure(configureOptionsAction);
        pulseBuilder.Services.AddHostedService<WebSocketHttpListenerTransportHostedService>();

        return pulseBuilder;
    }
}
