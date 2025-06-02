using MediatR;
using Microsoft.Extensions.DependencyInjection;
using Rymote.Pulse.Core;
using Rymote.Pulse.Core.Connections;

namespace Rymote.Pulse.MediatR;

public static class PulseMediatRExtensions
{
    /// <summary>
    /// Maps a MediatR request handler as a Pulse RPC endpoint
    /// </summary>
    public static void MapMediatRRequest<TRequest, TResponse>(
        this PulseDispatcher dispatcher,
        string handle,
        string version = "v1")
        where TRequest : class, IRequest<TResponse>, new()
        where TResponse : class, new()
    {
        dispatcher.MapRpc<TRequest, TResponse>(
            handle,
            async (request, context) =>
            {
                var (mediator, contextAccessor) = GetServices(context.Connection);
                
                // Ensure context is set
                contextAccessor.Context = context;
                
                return await mediator.Send(request);
            },
            version);
    }

    /// <summary>
    /// Maps a MediatR notification as a Pulse event endpoint
    /// </summary>
    public static void MapMediatRNotification<TNotification>(
        this PulseDispatcher dispatcher,
        string handle,
        string version = "v1")
        where TNotification : class, INotification, new()
    {
        dispatcher.MapEvent<TNotification>(
            handle,
            async (notification, context) =>
            {
                var (mediator, contextAccessor) = GetServices(context.Connection);
                
                // Ensure context is set
                contextAccessor.Context = context;
                
                await mediator.Publish(notification);
            },
            version);
    }

    /// <summary>
    /// Maps a MediatR stream request handler as a Pulse streaming RPC endpoint
    /// </summary>
    public static void MapMediatRStreamRequest<TRequest, TResponse>(
        this PulseDispatcher dispatcher,
        string handle,
        string version = "v1")
        where TRequest : class, IStreamRequest<TResponse>, new()
        where TResponse : class, new()
    {
        dispatcher.MapRpcStream<TRequest, TResponse>(
            handle,
            (request, context) => CreateMediatRStream<TRequest, TResponse>(request, context),
            version);
    }

    private static async IAsyncEnumerable<TResponse> CreateMediatRStream<TRequest, TResponse>(
        TRequest request,
        PulseContext context)
        where TRequest : class, IStreamRequest<TResponse>, new()
        where TResponse : class, new()
    {
        var (mediator, contextAccessor) = GetServices(context.Connection);
        
        // Ensure context is set
        contextAccessor.Context = context;
        
        await foreach (var response in mediator.CreateStream(request))
        {
            yield return response;
        }
    }

    /// <summary>
    /// Sends a Pulse event that triggers a MediatR notification
    /// </summary>
    public static async Task PublishMediatRNotificationAsync<TNotification>(
        this PulseContext context,
        string handle,
        TNotification notification,
        string version = "v1",
        CancellationToken cancellationToken = default)
        where TNotification : class, INotification, new()
    {
        await context.SendEventAsync(handle, notification, version, cancellationToken);
    }

    private static (IMediator mediator, IPulseContextAccessor contextAccessor) GetServices(PulseConnection connection)
    {
        if (!connection.TryGetMetadata<IServiceScope>("__scope", out var scope) || scope == null)
            throw new InvalidOperationException("Service scope not found in connection context");
        
        var mediator = scope.ServiceProvider.GetRequiredService<IMediator>();
        var contextAccessor = scope.ServiceProvider.GetRequiredService<IPulseContextAccessor>();
        
        return (mediator, contextAccessor);
    }
} 