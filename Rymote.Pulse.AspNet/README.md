# Rymote.Pulse.AspNet

ASP.NET Core integration for the Rymote.Pulse framework, providing WebSocket middleware and seamless integration with the ASP.NET Core pipeline.

## Overview

Rymote.Pulse.AspNet enables easy integration of Pulse messaging capabilities into ASP.NET Core applications. It provides:
- WebSocket middleware for handling Pulse protocol
- Integration with ASP.NET Core's dependency injection
- Request pipeline integration
- Automatic connection lifecycle management

## Installation

```bash
dotnet add package Rymote.Pulse.AspNet
```

## Quick Start

### Basic Setup

```csharp
using Rymote.Pulse.AspNet;
using Rymote.Pulse.Core;

var builder = WebApplication.CreateBuilder(args);

// Register Pulse services
builder.Services.AddSingleton<IPulseLogger, MyPulseLogger>();
builder.Services.AddSingleton<PulseConnectionManager>();
builder.Services.AddSingleton<PulseDispatcher>(sp =>
{
    var connectionManager = sp.GetRequiredService<PulseConnectionManager>();
    var logger = sp.GetRequiredService<IPulseLogger>();
    return new PulseDispatcher(connectionManager, logger);
});

var app = builder.Build();

// Get dispatcher and configure handlers
var dispatcher = app.Services.GetRequiredService<PulseDispatcher>();
var logger = app.Services.GetRequiredService<IPulseLogger>();

// Map handlers
dispatcher.MapRpc<EchoRequest, EchoResponse>("echo",
    async (request, context) => new EchoResponse { Message = request.Message });

// Use Pulse WebSocket middleware
app.UsePulseProtocol("/pulse", dispatcher, logger);

app.Run();
```

## Middleware Configuration

### UsePulseProtocol Extension

The `UsePulseProtocol` extension method configures the WebSocket middleware:

```csharp
app.UsePulseProtocol(
    websocketPath: "/pulse",      // WebSocket endpoint path
    pulseDispatcher: dispatcher,   // Configured PulseDispatcher
    pulseLogger: logger           // IPulseLogger implementation
);
```

### Multiple Endpoints

You can configure multiple Pulse endpoints with different dispatchers:

```csharp
// Public API endpoint
app.UsePulseProtocol("/api/pulse", publicDispatcher, logger);

// Admin endpoint with different handlers
app.UsePulseProtocol("/admin/pulse", adminDispatcher, logger);
```

## Integration with ASP.NET Core Features

### Dependency Injection

```csharp
// Register services
builder.Services.AddScoped<IUserService, UserService>();
builder.Services.AddSingleton<IPulseLogger, SerilogPulseLogger>();

// Create dispatcher with DI
builder.Services.AddSingleton<PulseDispatcher>(sp =>
{
    var connectionManager = sp.GetRequiredService<PulseConnectionManager>();
    var logger = sp.GetRequiredService<IPulseLogger>();
    var dispatcher = new PulseDispatcher(connectionManager, logger);
    
    // Configure handlers with injected services
    var userService = sp.GetRequiredService<IUserService>();
    
    dispatcher.MapRpc<GetUserRequest, UserResponse>("users.get",
        async (request, context) =>
        {
            var user = await userService.GetUserAsync(request.UserId);
            return new UserResponse { Id = user.Id, Name = user.Name };
        });
    
    return dispatcher;
});
```

### Authentication Integration

```csharp
// Use ASP.NET Core authentication
app.UseAuthentication();
app.UseAuthorization();

// Add authentication middleware to Pulse
dispatcher.Use(async (context, next) =>
{
    // Access HttpContext from connection metadata
    if (context.Connection.TryGetMetadata<HttpContext>("HttpContext", out var httpContext))
    {
        if (!httpContext.User.Identity?.IsAuthenticated ?? true)
        {
            throw new PulseException(PulseStatus.UNAUTHORIZED, "Authentication required");
        }
        
        // Store user info in connection metadata
        context.Connection.SetMetadata("UserId", httpContext.User.FindFirst("sub")?.Value);
    }
    
    await next();
});
```

### CORS Configuration

```csharp
builder.Services.AddCors(options =>
{
    options.AddPolicy("PulsePolicy", policy =>
    {
        policy.WithOrigins("https://example.com")
              .AllowAnyHeader()
              .AllowAnyMethod()
              .AllowCredentials(); // Required for WebSocket
    });
});

app.UseCors("PulsePolicy");
app.UsePulseProtocol("/pulse", dispatcher, logger);
```

## Connection Lifecycle

The middleware automatically manages connection lifecycle:

1. **Connection Established**: When a WebSocket connection is accepted
2. **Connection Added**: Registered with PulseConnectionManager
3. **Message Processing**: Handles incoming messages through the dispatcher
4. **Connection Cleanup**: Automatically removed on disconnect

```csharp
// Lifecycle events in logs
"Client connected: {connectionId}"
"Client disconnected: {connectionId}"
"Client initiated close. Status: {status}, Description: {description}"
```

## Error Handling

The middleware provides comprehensive error handling:

### Client Errors
```csharp
// Non-WebSocket requests
"Rejected non-WebSocket request on path /pulse"
// Returns 400 Bad Request

// Message processing errors
"Error processing incoming Pulse message"
// Returns error response to client
```

### Connection Errors
```csharp
// WebSocket errors
"Socket exception"
// Handles graceful disconnection

// Protocol errors
"Failed to deserialize invalid incoming envelope"
// Sends BAD_REQUEST response
```

## Memory Management

The AspNet integration includes memory-efficient features:

### ArrayPool Buffer Management
- Uses `ArrayPool<byte>` for efficient buffer allocation
- Adaptive buffer sizing based on message patterns
- Automatic buffer return in all code paths

### Message Size Limits
- Default 10MB maximum message size
- Configurable per deployment
- Automatic connection closure for oversized messages

### Resource Cleanup
- Proper disposal of WebSocket connections
- Automatic cleanup in finally blocks
- Buffer return even in error scenarios

## Performance Considerations

### Buffer Settings
```csharp
const int BufferSizeInBytes = 4096;      // Initial buffer size
const int MaxMessageSize = 10485760;     // 10MB max message
```

### Connection Pooling
- Reuses buffers via ArrayPool
- Efficient message accumulation
- Minimal allocations per message

## Logging Integration

Integrate with ASP.NET Core logging:

```csharp
public class AspNetCorePulseLogger : IPulseLogger
{
    private readonly ILogger<AspNetCorePulseLogger> _logger;
    
    public AspNetCorePulseLogger(ILogger<AspNetCorePulseLogger> logger)
    {
        _logger = logger;
    }
    
    public void LogInfo(string message) => 
        _logger.LogInformation(message);
        
    public void LogWarning(string message) => 
        _logger.LogWarning(message);
        
    public void LogError(string message, Exception? exception = null) => 
        _logger.LogError(exception, message);
}

// Register in DI
builder.Services.AddSingleton<IPulseLogger, AspNetCorePulseLogger>();
```

## Health Checks

Add health checks for Pulse endpoints:

```csharp
builder.Services.AddHealthChecks()
    .AddCheck("pulse", () =>
    {
        var connectionManager = app.Services.GetRequiredService<PulseConnectionManager>();
        var connections = connectionManager.GetAllConnectionIdsAsync().Result;
        
        return connections.Count() < 10000 
            ? HealthCheckResult.Healthy($"Active connections: {connections.Count()}")
            : HealthCheckResult.Degraded($"High connection count: {connections.Count()}");
    });

app.MapHealthChecks("/health");
```

## Deployment Considerations

### IIS Hosting
- Enable WebSocket protocol in IIS
- Configure application pool recycling settings
- Set appropriate connection timeouts

### Reverse Proxy (nginx/Apache)
- Enable WebSocket proxy support
- Configure appropriate buffer sizes
- Set connection upgrade headers

### Docker/Kubernetes
- Expose WebSocket ports
- Configure health checks
- Set resource limits appropriately

## Troubleshooting

### Common Issues

1. **400 Bad Request**
   - Ensure WebSocket support is enabled
   - Check that path matches configuration
   - Verify CORS settings for cross-origin requests

2. **Connection Drops**
   - Check firewall/proxy timeout settings
   - Verify keep-alive configuration
   - Monitor for memory/resource constraints

3. **Message Size Errors**
   - Adjust MaxMessageSize if needed
   - Consider using streaming for large data
   - Monitor buffer pool usage

## Dependencies

- .NET 9.0 or later
- Rymote.Pulse.Core
- Microsoft.AspNetCore.WebSockets

## License

This project is licensed under the MIT License. 