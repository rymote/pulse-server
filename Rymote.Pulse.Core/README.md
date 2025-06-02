# Rymote.Pulse.Core

The core library of the Rymote.Pulse framework, providing the fundamental building blocks for real-time WebSocket-based messaging in .NET applications.

## Overview

Rymote.Pulse.Core contains the essential components for building real-time messaging applications:
- Connection and group management
- Message dispatching and routing
- Protocol implementation
- Middleware pipeline
- Serialization infrastructure
- Clustering abstractions

## Installation

```bash
dotnet add package Rymote.Pulse.Core
```

## Key Components

### PulseDispatcher

The central message router that handles incoming messages and routes them to appropriate handlers.

```csharp
var dispatcher = new PulseDispatcher(connectionManager, logger);

// Map RPC handler
dispatcher.MapRpc<RequestType, ResponseType>("handle.name", 
    async (request, context) => new ResponseType { /* ... */ });

// Map streaming handler
dispatcher.MapRpcStream<RequestType, ResponseType>("stream.name",
    async function* (request, context) 
    {
        yield return new ResponseType { /* ... */ };
    });

// Map event handler
dispatcher.MapEvent<EventType>("event.name",
    async (eventData, context) => { /* ... */ });
```

### PulseConnectionManager

Manages WebSocket connections and connection groups.

```csharp
var connectionManager = new PulseConnectionManager(clusterStore, nodeId, logger);

// Add connection
var connection = await connectionManager.AddConnectionAsync(connectionId, webSocket);

// Group management
await connectionManager.AddToGroupAsync("group-name", connection);
await connectionManager.RemoveFromGroupAsync("group-name", connectionId);

// Get connection
var connection = connectionManager.GetConnection(connectionId);
```

### PulseContext

Provides contextual information about the current message being processed.

```csharp
// Available in handlers via the context parameter
public async Task<Response> HandleRequest(Request req, PulseContext context)
{
    // Access connection info
    var connectionId = context.Connection.ConnectionId;
    
    // Access connection manager
    var group = context.ConnectionManager.GetOrCreateGroup("users");
    
    // Send events
    await context.SendEventAsync(group, "notification", new { Message = "Hello" });
    
    // Use logger
    context.Logger.LogInfo($"Processing request from {connectionId}");
    
    return new Response { /* ... */ };
}
```

### Middleware Pipeline

Extensible middleware system for cross-cutting concerns.

```csharp
public class AuthenticationMiddleware
{
    public async Task InvokeAsync(PulseContext context, Func<Task> next)
    {
        // Pre-processing
        var token = context.UntypedRequest.AuthToken;
        if (!IsValidToken(token))
        {
            throw new PulseException(PulseStatus.UNAUTHORIZED, "Invalid token");
        }
        
        // Call next middleware
        await next();
        
        // Post-processing
    }
}

// Register middleware
dispatcher.Use(async (context, next) => 
{
    var middleware = new AuthenticationMiddleware();
    await middleware.InvokeAsync(context, next);
});
```

## Message Types

### PulseEnvelope

The base message envelope that wraps all messages.

```csharp
public class PulseEnvelope<T>
{
    public string? Id { get; set; }
    public string Handle { get; set; }
    public T Body { get; set; }
    public string? AuthToken { get; set; }
    public PulseKind Kind { get; set; }
    public string Version { get; set; }
    public string? ClientCorrelationId { get; set; }
    public PulseStatus? Status { get; set; }
    public string? Error { get; set; }
}
```

### PulseKind

Enumeration of message types:
- `RPC` - Request/Response pattern
- `STREAM` - Streaming messages
- `EVENT` - Fire-and-forget events

### PulseStatus

Status codes for responses:
- `OK` - Success
- `BAD_REQUEST` - Client error
- `UNAUTHORIZED` - Authentication required
- `NOT_FOUND` - Handler not found
- `TIMEOUT` - Operation timed out
- `INTERNAL_ERROR` - Server error

## Serialization

Uses MessagePack for efficient binary serialization:

```csharp
// Serialize
byte[] data = MsgPackSerdes.Serialize(envelope);

// Deserialize
var envelope = MsgPackSerdes.Deserialize<PulseEnvelope<MyType>>(data);
```

## Clustering Support

Optional clustering for horizontal scaling:

```csharp
public interface IClusterStore
{
    Task AddConnectionAsync(string connectionId, string nodeId);
    Task RemoveConnectionAsync(string connectionId);
    Task<Dictionary<string, string>> GetAllConnectionsAsync();
    Task AddToGroupAsync(string groupName, string connectionId, string nodeId);
    Task RemoveFromGroupAsync(string groupName, string connectionId);
    Task<Dictionary<string, HashSet<string>>> GetGroupMembersAsync(string groupName);
}
```

## Memory Management Features

### ArrayPool Integration
- Efficient buffer management for message processing
- Reduces GC pressure in high-throughput scenarios

### Automatic Cleanup
- Abandoned streams cleaned up after 5-minute timeout
- Empty groups automatically removed
- Connection metadata limited to 100 entries

### Resource Disposal
- Proper implementation of IDisposable patterns
- Automatic cleanup of resources on shutdown

## Error Handling

```csharp
try
{
    await dispatcher.ProcessRawAsync(connection, messageBytes);
}
catch (PulseException ex)
{
    // Handle Pulse-specific exceptions
    logger.LogError($"Pulse error: {ex.Status} - {ex.Message}");
}
catch (Exception ex)
{
    // Handle general exceptions
    logger.LogError("Unexpected error", ex);
}
```

## Thread Safety

All public APIs are thread-safe and designed for concurrent access:
- `ConcurrentDictionary` for connection and group storage
- Thread-safe cleanup timers
- Async/await throughout for proper synchronization

## Performance Tips

1. **Message Size**: Keep messages under 10MB (default limit)
2. **Buffering**: Use streaming for large data transfers
3. **Groups**: Remove connections from groups when no longer needed
4. **Metadata**: Limit connection metadata usage
5. **Handlers**: Keep handlers lightweight and async

## Extension Points

### Custom Logger
```csharp
public class CustomLogger : IPulseLogger
{
    public void LogInfo(string message) { /* ... */ }
    public void LogWarning(string message) { /* ... */ }
    public void LogError(string message, Exception? exception = null) { /* ... */ }
}
```

### Custom Middleware
```csharp
dispatcher.Use(async (context, next) =>
{
    // Custom logic
    await next();
});
```

### Custom Serialization
Implement custom serialization by modifying `MsgPackSerdes` or creating your own serialization layer.

## Dependencies

- .NET 9.0 or later
- MessagePack-CSharp (2.5.187)
- System.Threading.Channels

## License

This project is licensed under the MIT License. 