using MediatR;
using Microsoft.Extensions.DependencyInjection;
using Rymote.Pulse.Core;
using Rymote.Pulse.MediatR;

namespace Rymote.Pulse.MediatR.Examples;

// Example MediatR request/response
public class GetUserByIdRequest : IRequest<UserResponse>
{
    public int UserId { get; set; }
}

public class UserResponse
{
    public int Id { get; set; }
    public string Name { get; set; } = "";
    public string Email { get; set; } = "";
}

// Example MediatR notification (event)
public class UserLoggedInNotification : INotification
{
    public int UserId { get; set; }
    public DateTime LoginTime { get; set; }
}

// Example event for broadcasting
public class UserActivityEvent
{
    public int UserId { get; set; }
    public string Activity { get; set; } = "";
}

// Example stream request
public class GetRealtimeDataRequest : IStreamRequest<DataChunk>
{
    public string DataType { get; set; } = "";
}

public class DataChunk
{
    public string Data { get; set; } = "";
    public DateTime Timestamp { get; set; }
}

// Example handlers that use PulseContext
public class GetUserByIdHandler : IRequestHandler<GetUserByIdRequest, UserResponse>
{
    private readonly IPulseContextAccessor _pulseContextAccessor;
    
    public GetUserByIdHandler(IPulseContextAccessor pulseContextAccessor)
    {
        _pulseContextAccessor = pulseContextAccessor;
    }
    
    public Task<UserResponse> Handle(GetUserByIdRequest request, CancellationToken cancellationToken)
    {
        // Access the PulseContext
        var context = _pulseContextAccessor.Context;
        if (context != null)
        {
            // You can access connection info, metadata, etc.
            var connectionId = context.Connection.ConnectionId;
            context.Logger.LogInfo($"Handling GetUserById for connection: {connectionId}");
            
            // You can also set metadata on the connection
            context.Connection.SetMetadata("last-user-request", request.UserId);
        }
        
        return Task.FromResult(new UserResponse 
        { 
            Id = request.UserId, 
            Name = "John Doe",
            Email = "john@example.com"
        });
    }
}

public class UserLoggedInHandler : INotificationHandler<UserLoggedInNotification>
{
    private readonly IPulseContextAccessor _pulseContextAccessor;
    
    public UserLoggedInHandler(IPulseContextAccessor pulseContextAccessor)
    {
        _pulseContextAccessor = pulseContextAccessor;
    }
    
    public async Task Handle(UserLoggedInNotification notification, CancellationToken cancellationToken)
    {
        var context = _pulseContextAccessor.Context;
        if (context != null)
        {
            // Send an event to all connections in a group
            var group = context.ConnectionManager.GetOrCreateGroup("authenticated-users");
            await context.SendEventAsync(
                group, 
                "user.activity", 
                new UserActivityEvent { UserId = notification.UserId, Activity = "login" }
            );
        }
        
        Console.WriteLine($"User {notification.UserId} logged in at {notification.LoginTime}");
    }
}

public class ExampleUsage
{
    public static void ConfigureServices(IServiceCollection services)
    {
        // Add MediatR with Pulse integration
        services.AddPulseMediatR(options =>
        {
            options.ConfigureMediatR = config =>
            {
                config.RegisterServicesFromAssemblyContaining<GetUserByIdHandler>();
            };
        });
        
        // Add any other services your handlers need
        // services.AddScoped<IUserService, UserService>();
    }

    public static void ConfigureDispatcher(PulseDispatcher dispatcher, IServiceProvider serviceProvider)
    {
        // Enable MediatR middleware
        dispatcher.UseMediatR(serviceProvider);

        // Map RPC endpoints with single line
        dispatcher.MapMediatRRequest<GetUserByIdRequest, UserResponse>("users.getById");
        
        // Map event endpoints with single line
        dispatcher.MapMediatRNotification<UserLoggedInNotification>("users.loggedIn");
        
        // Map streaming endpoints (if IStreamRequest is implemented in MediatR)
        // dispatcher.MapMediatRStreamRequest<GetRealtimeDataRequest, DataChunk>("data.stream");
    }

    public static async Task SendEventsExample(PulseContext context)
    {
        // Send an event that will be handled by MediatR
        await context.PublishMediatRNotificationAsync(
            "users.loggedIn",
            new UserLoggedInNotification 
            { 
                UserId = 123, 
                LoginTime = DateTime.UtcNow 
            });
    }
} 