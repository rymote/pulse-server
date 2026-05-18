# Rymote.Pulse.AspNet

ASP.NET Core integration for the Rymote.Pulse framework, providing WebSocket middleware and seamless integration with the ASP.NET Core pipeline.

## Overview

Rymote.Pulse.AspNet enables easy integration of Pulse messaging capabilities into ASP.NET Core applications. It provides:
- WebSocket middleware for handling the Pulse protocol
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
using Rymote.Pulse.Core.Logging;

var builder = WebApplication.CreateBuilder(args);

// Register Pulse services
builder.Services.AddSingleton<IPulseLogger>(new PulseConsoleLogger(enableDebugLogs: false));
builder.Services.AddSingleton<PulseConnectionManager>();
builder.Services.AddSingleton<PulseDispatcher>(serviceProvider =>
{
    var connectionManager = serviceProvider.GetRequiredService<PulseConnectionManager>();
    var logger = serviceProvider.GetRequiredService<IPulseLogger>();
    return new PulseDispatcher(connectionManager, logger);
});

var app = builder.Build();

// Get dispatcher and configure handlers
var dispatcher = app.Services.GetRequiredService<PulseDispatcher>();
var logger = app.Services.GetRequiredService<IPulseLogger>();

// Map handlers
dispatcher.MapRpc<EchoRequest, EchoResponse>("echo",
    async (request, context) => new EchoResponse { Message = request.Message });

// Use Pulse WebSocket middleware (options callback is optional)
app.UsePulseProtocol("/pulse", dispatcher, logger);

app.Run();
```

## Middleware Configuration

### UsePulseProtocol Extension

The `UsePulseProtocol` extension method configures the WebSocket middleware:

```csharp
app.UsePulseProtocol(
    websocketPath: "/pulse",                          // WebSocket endpoint path
    pulseDispatcher: dispatcher,                      // Configured PulseDispatcher
    pulseLogger: logger,                              // IPulseLogger implementation
    configureOptionsAction: options =>                // Optional options callback
    {
        options.BufferSizeInBytes = 4 * 1024;
        options.MaxMessageSizeInBytes = 10 * 1024 * 1024;
    });
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
builder.Services.AddSingleton<IPulseLogger, AspNetCorePulseLogger>();

// Create dispatcher with DI
builder.Services.AddSingleton<PulseDispatcher>(serviceProvider =>
{
    var connectionManager = serviceProvider.GetRequiredService<PulseConnectionManager>();
    var logger = serviceProvider.GetRequiredService<IPulseLogger>();
    var dispatcher = new PulseDispatcher(connectionManager, logger);
    
    // Configure handlers with injected services
    var userService = serviceProvider.GetRequiredService<IUserService>();
    
    dispatcher.MapRpc<GetUserRequest, UserResponse>("users.get",
        async (request, context) =>
        {
            var user = await userService.GetUserAsync(request.UserId);
            return new UserResponse { Id = user.Id, Name = user.Name };
        });
    
    return dispatcher;
});
```

For per-request scoping, the `Rymote.Pulse.Attributes` package can register a scope-management middleware automatically when you pass an `IServiceProvider` to `RegisterHandlersFromAssembly`.

### Authentication Integration

The middleware automatically stores the originating `HttpContext` (and a few other request facts) in connection metadata, so you can run an ASP.NET authentication check from a Pulse middleware:

```csharp
// Use ASP.NET Core authentication
app.UseAuthentication();
app.UseAuthorization();

// Add authentication middleware to Pulse
dispatcher.Use(async (context, next) =>
{
    if (context.Connection.TryGetMetadata<HttpContext>("http_context", out var httpContext) && httpContext != null)
    {
        if (!httpContext.User.Identity?.IsAuthenticated ?? true)
        {
            throw new PulseException(PulseStatus.UNAUTHORIZED, "Authentication required");
        }
        
        // Store user info in connection metadata
        context.Connection.SetMetadata("user_id", httpContext.User.FindFirst("sub")?.Value);
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

1. **Connection Established** — when a WebSocket upgrade is accepted
2. **Connection Registered** — added to `PulseConnectionManager` with the HTTP request's query parameters
3. **Metadata Populated** — the following keys are set automatically:
   - `http_context` — the originating `HttpContext`
   - `ip_address` — the client IP (honoring `X-Forwarded-For` / `X-Real-IP`)
   - `user_agent` — the `User-Agent` header
   - `origin` — the `Origin` header
   - `connected_at` — UTC timestamp of the upgrade
4. **OnConnect Handlers Run** — registered via `dispatcher.AddOnConnectHandler(...)`
5. **Message Processing** — incoming binary messages are handed to the dispatcher
6. **OnDisconnect Handlers Run** — registered via `dispatcher.AddOnDisconnectHandler(...)`
7. **Connection Cleanup** — automatically removed on disconnect

Log output looks like:

```csharp
"[{connectionId}] Client connected: IP: {ipAddress} | Origin: {origin} | UserAgent: {userAgent}"
"[{connectionId}] Client disconnected: IP: {ipAddress} | Duration: {duration}"
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
// Returns error response to client (for RPC messages)
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

If an `OnConnect` handler throws, the middleware closes the socket with `WebSocketCloseStatus.PolicyViolation` and the exception message.

## Memory Management

The AspNet integration includes memory-efficient features:

### ArrayPool Buffer Management
- Uses `ArrayPool<byte>.Shared` for receive and message-assembly buffers
- Per-segment buffers are also rented from the shared pool
- All rented buffers are returned in finally blocks, including error paths

### Message Size Limits
- Default 10 MiB maximum message size (configurable via `PulseProtocolOptions.MaxMessageSizeInBytes`)
- The socket is closed with `WebSocketCloseStatus.MessageTooBig` when a frame exceeds the limit

### Resource Cleanup
- Proper disposal of WebSocket connections
- Automatic cleanup in finally blocks
- Buffer return even in error scenarios

## Performance Considerations

### Buffer Settings
```csharp
options.BufferSizeInBytes = 4 * 1024;       // Initial receive buffer (default 4 KiB)
options.MaxMessageSizeInBytes = 10 * 1024 * 1024; // 10 MiB max message (default)
```

### Connection Pooling
- Reuses receive buffers via ArrayPool
- Single assembly buffer per connection, sized to the configured max message
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
    
    public void LogDebug(string message) =>
        _logger.LogDebug(message);
        
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
        var connections = connectionManager.GetAllConnectionIdsAsync().GetAwaiter().GetResult();
        
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
   - Adjust `PulseProtocolOptions.MaxMessageSizeInBytes` if needed
   - Split large payloads at the application layer
   - Monitor buffer pool usage

## Dependencies

- .NET 10.0 or later
- Rymote.Pulse.Core
- Microsoft.AspNetCore.App (framework reference)

## License

This project is licensed under the BSD 3-Clause License.
