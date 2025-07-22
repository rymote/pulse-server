using MediatR;
using Microsoft.Extensions.DependencyInjection;
using Rymote.Pulse.Core;
using Rymote.Pulse.Core.Connections;

namespace Rymote.Pulse.MediatR;

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Adds Pulse with MediatR integration
    /// </summary>
    public static IServiceCollection AddPulseMediatR(
        this IServiceCollection services,
        Action<PulseMediatROptions>? configureOptions = null)
    {
        PulseMediatROptions options = new PulseMediatROptions();
        configureOptions?.Invoke(options);

        if (!services.Any(x => x.ServiceType == typeof(IMediator)))
        {
            services.AddMediatR(config =>
            {
                options.ConfigureMediatR?.Invoke(config);
            });
        }

        services.AddScoped<IPulseContextAccessor, PulseContextAccessor>();
        services.AddSingleton<IPulseMediatRMiddleware>(sp => new PulseMediatRMiddleware(sp));

        return services;
    }

    public static PulseDispatcher UseMediatR(
        this PulseDispatcher dispatcher,
        IServiceProvider serviceProvider)
    {
        IPulseMediatRMiddleware middleware = serviceProvider.GetRequiredService<IPulseMediatRMiddleware>();
        dispatcher.Use(async (context, next) => await middleware.InjectMediatR(context, next));
        return dispatcher;
    }
}

public class PulseMediatROptions
{
    public Action<MediatRServiceConfiguration>? ConfigureMediatR { get; set; }
}

public interface IPulseMediatRMiddleware
{
    Task InjectMediatR(PulseContext context, Func<Task> next);
}

internal class PulseMediatRMiddleware : IPulseMediatRMiddleware
{
    private readonly IServiceProvider _serviceProvider;

    public PulseMediatRMiddleware(IServiceProvider serviceProvider)
    {
        _serviceProvider = serviceProvider;
    }

    public async Task InjectMediatR(PulseContext context, Func<Task> next)
    {
        IServiceScope scope = _serviceProvider.CreateScope();
        IPulseContextAccessor contextAccessor = scope.ServiceProvider.GetRequiredService<IPulseContextAccessor>();
        
        contextAccessor.Context = context;
        
        context.Connection.SetMetadata("__scope", scope);
        
        try
        {
            await next();
        }
        finally
        {
            context.Connection.RemoveMetadata("__scope");
            contextAccessor.Context = null;
            
            scope.Dispose();
        }
    }
} 