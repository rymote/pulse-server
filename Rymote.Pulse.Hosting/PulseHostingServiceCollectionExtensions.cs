using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Rymote.Pulse.Core;
using Rymote.Pulse.Core.Cluster;
using Rymote.Pulse.Core.Connections;
using Rymote.Pulse.Core.Logging;

namespace Rymote.Pulse.Hosting;

public static class PulseHostingServiceCollectionExtensions
{
    public static IServiceCollection AddPulse(
        this IServiceCollection serviceCollection,
        Action<PulseHostingOptions> configureOptionsAction)
    {
        ArgumentNullException.ThrowIfNull(serviceCollection);
        ArgumentNullException.ThrowIfNull(configureOptionsAction);

        serviceCollection.Configure(configureOptionsAction);

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

        serviceCollection.AddHostedService<PulseHostedService>();

        return serviceCollection;
    }
}
