using Microsoft.Extensions.DependencyInjection;
using Rymote.Pulse.Hosting;

namespace Rymote.Pulse.Transports.RawTcp;

public static class RawTcpTransportServiceCollectionExtensions
{
    public static IPulseBuilder AddRawTcpTransport(
        this IPulseBuilder pulseBuilder,
        Action<RawTcpTransportOptions> configureOptionsAction)
    {
        ArgumentNullException.ThrowIfNull(pulseBuilder);
        ArgumentNullException.ThrowIfNull(configureOptionsAction);

        pulseBuilder.Services.Configure(configureOptionsAction);
        pulseBuilder.Services.AddHostedService<RawTcpTransportHostedService>();

        return pulseBuilder;
    }
}
