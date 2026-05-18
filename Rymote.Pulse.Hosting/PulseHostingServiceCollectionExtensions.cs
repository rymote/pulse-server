using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Rymote.Pulse.Core;
using Rymote.Pulse.Core.Cluster;
using Rymote.Pulse.Core.Connections;
using Rymote.Pulse.Core.Logging;

namespace Rymote.Pulse.Hosting;

public static class PulseHostingServiceCollectionExtensions
{
    public static IPulseBuilder AddPulse(this IServiceCollection serviceCollection)
    {
        ArgumentNullException.ThrowIfNull(serviceCollection);

        serviceCollection.TryAddSingleton<IPulseLogger>(_ => new PulseConsoleLogger(enableDebugLogs: false));

        serviceCollection.TryAddSingleton<PulseConnectionManager>(serviceProvider =>
        {
            IPulseClusterStore? clusterStore = serviceProvider.GetService<IPulseClusterStore>();
            IPulseLogger logger = serviceProvider.GetRequiredService<IPulseLogger>();
            return new PulseConnectionManager(clusterStore, nodeId: null, logger);
        });

        serviceCollection.TryAddSingleton<PulseDispatcher>(serviceProvider =>
        {
            PulseConnectionManager connectionManager = serviceProvider.GetRequiredService<PulseConnectionManager>();
            IPulseLogger logger = serviceProvider.GetRequiredService<IPulseLogger>();
            return new PulseDispatcher(connectionManager, logger);
        });

        return new PulseBuilder(serviceCollection);
    }
}
