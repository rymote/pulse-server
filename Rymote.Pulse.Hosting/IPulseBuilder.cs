using Microsoft.Extensions.DependencyInjection;

namespace Rymote.Pulse.Hosting;

public interface IPulseBuilder
{
    IServiceCollection Services { get; }
}
