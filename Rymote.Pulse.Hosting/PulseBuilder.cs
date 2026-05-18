using Microsoft.Extensions.DependencyInjection;

namespace Rymote.Pulse.Hosting;

internal sealed class PulseBuilder : IPulseBuilder
{
    public IServiceCollection Services { get; }

    public PulseBuilder(IServiceCollection services)
    {
        Services = services;
    }
}
