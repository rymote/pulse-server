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
        var options = new PulseMediatROptions();
        configureOptions?.Invoke(options);

        // Add MediatR if not already added
        if (!services.Any(x => x.ServiceType == typeof(IMediator)))
        {
            services.AddMediatR(config =>
            {
                options.ConfigureMediatR?.Invoke(config);
            });
        }

        // Add PulseContext accessor
        services.AddScoped<IPulseContextAccessor, PulseContextAccessor>();

        // Add Pulse middleware that injects IMediator into connections
        services.AddSingleton<IPulseMediatRMiddleware>(sp => new PulseMediatRMiddleware(sp));

        return services;
    }

    /// <summary>
    /// Configures the PulseDispatcher to use MediatR middleware
    /// </summary>
    public static PulseDispatcher UseMediatR(
        this PulseDispatcher dispatcher,
        IServiceProvider serviceProvider)
    {
        var middleware = serviceProvider.GetRequiredService<IPulseMediatRMiddleware>();
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
        // Create a scoped service provider for this request
        var scope = _serviceProvider.CreateScope();
        var contextAccessor = scope.ServiceProvider.GetRequiredService<IPulseContextAccessor>();
        
        // Set the current context
        contextAccessor.Context = context;
        
        // Store the scope in the connection context
        context.Connection.SetMetadata("__scope", scope);
        
        try
        {
            await next();
        }
        finally
        {
            // Clean up the references
            context.Connection.RemoveMetadata("__scope");
            contextAccessor.Context = null;
            
            // Dispose the scope
            scope.Dispose();
        }
    }
} 