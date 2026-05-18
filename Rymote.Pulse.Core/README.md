# Rymote.Pulse.Core

The core library of the Rymote.Pulse framework, providing the fundamental building blocks for real-time WebSocket-based messaging in .NET applications.

## Overview

Rymote.Pulse.Core contains the essential components for building real-time messaging applications:
- Connection and group management
- Message dispatching and routing
- Protocol implementation
- Middleware pipeline
- Serialization infrastructure (MessagePack + ChaCha20-Poly1305 AEAD framing)
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

// Map RPC handler (request + response)
dispatcher.MapRpc<RequestType, ResponseType>("handle.name",
    async (request, context) => new ResponseType { /* ... */ });

// Map RPC handler (response-only — for routes with parameters)
dispatcher.MapRpc<ResponseType>("items.{itemId}.get",
    async context =>
    {
        var itemId = context.GetRequiredParameter<string>("itemId");
        return new ResponseType { /* ... */ };
    });

// Map event handler
dispatcher.MapEvent<EventType>("event.name",
    async (eventData, context) => { /* ... */ });
```

A version string (default `"v1"`) can be passed to any `MapRpc` / `MapEvent` call to scope the handler to a specific envelope version. Handles containing `{name}`, `*`, or `[` are stored as compiled regex routes; all other handles use the fast lookup dictionary.

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

All constructor parameters are optional: `IPulseClusterStore?`, `string? nodeId` (defaults to `Environment.MachineName`), and `IPulseLogger?`.

### PulseContext

Provides contextual information about the current message being processed.

```csharp
// Available in handlers via the context parameter
public async Task<Response> HandleRequest(Request request, PulseContext context)
{
    // Access connection info
    string connectionId = context.Connection.ConnectionId;
    
    // Access connection manager
    PulseGroup group = context.ConnectionManager.GetOrCreateGroup("users");
    
    // Send events
    await context.SendEventAsync(group, "notification", new { Message = "Hello" });
    
    // Use logger
    context.Logger.LogInfo($"Processing request from {connectionId}");
    
    // Route parameters (when the handle contains placeholders like "users.{id}")
    string? userId = context.GetParameter<string>("id");
    
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
        string? token = context.UntypedRequest.AuthToken;
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

A built-in `ConcurrencyMiddleware` is also provided for capping in-flight request count with a wait timeout that surfaces as `PulseStatus.TIMEOUT`.

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
- `RPC` — Request/Response pattern
- `EVENT` — Fire-and-forget events

### PulseStatus

Status codes for responses:
- `OK` — Success
- `BAD_REQUEST` — Client error
- `UNAUTHORIZED` — Authentication required
- `NOT_FOUND` — Handler not found
- `INTERNAL_ERROR` — Server error
- `TIMEOUT` — Operation timed out
- `UNSUPPORTED` — Operation not supported
- `CONFLICT` — Conflicting state

## Serialization

Bodies are encoded with MessagePack and then wrapped in a ChaCha20-Poly1305 AEAD frame (`nonce(12) | ciphertext | tag(16)`). The same `MsgPackSerdes` API is used for both directions:

```csharp
// Serialize (MessagePack + AEAD frame)
byte[] data = MsgPackSerdes.Serialize(envelope);

// Deserialize (verifies the AEAD tag and decodes the payload)
var envelope = MsgPackSerdes.Deserialize<PulseEnvelope<MyType>>(data);
```

`MsgPackSerdes` pools `ArrayBufferWriter<byte>` instances and uses a thread-local `RandomNumberGenerator` for nonce generation.

## Clustering Support

Optional abstractions for horizontal scaling. Pulse does not bundle a transport; provide your own implementation backed by Redis, NATS, or any other coordinator.

```csharp
public interface IPulseClusterStore
{
    Task AddConnectionAsync(string connectionId, string nodeId);
    Task RemoveConnectionAsync(string connectionId);
    Task AddToGroupAsync(string groupName, string connectionId, string nodeId);
    Task RemoveFromGroupAsync(string groupName, string connectionId);
    Task<Dictionary<string, string>> GetAllConnectionsAsync();
    Task<Dictionary<string, string>> GetGroupMembersAsync(string groupName);
    Task<HashSet<string>> GetGroupNodesAsync(string groupName);
}

public interface IPulseClusterMessaging
{
    string CurrentNodeId { get; }
    Task SendToNodeAsync(string nodeId, byte[] message, CancellationToken cancellationToken = default);
    Task BroadcastToClusterAsync(byte[] message, CancellationToken cancellationToken = default);
    Task SubscribeToClusterMessagesAsync(Func<string, byte[], Task> messageHandler, CancellationToken cancellationToken = default);
}
```

A `PulseClusterMessage` envelope (with `PulseClusterMessageType` of `GROUP_BROADCAST`, `DIRECT_MESSAGE`, or `HEALTH_CHECK`) is provided as a building block for inter-node payloads.

## Memory Management Features

### ArrayPool Integration
- Pooled receive and message-assembly buffers reduce GC pressure on the hot path.

### Pooled Serialization Writers
- `MsgPackSerdes` reuses `ArrayBufferWriter<byte>` instances sized to processor count.

### Automatic Cleanup
- Empty groups are removed when their last member leaves.
- Each `PulseGroup` runs a periodic sweep every 30 seconds to prune closed or failed connections.
- Connection metadata is capped at 100 entries per connection.

### Resource Disposal
- Proper implementation of IDisposable patterns
- Automatic cleanup of resources on shutdown

## Error Handling

```csharp
try
{
    await dispatcher.ProcessRawAsync(connection, messageBytes);
}
catch (PulseException pulseException)
{
    // Handle Pulse-specific exceptions
    logger.LogError($"Pulse error: {pulseException.Status} - {pulseException.Message}");
}
catch (Exception exception)
{
    // Handle general exceptions
    logger.LogError("Unexpected error", exception);
}
```

Internally, `ErrorMapper.MapException` translates known exceptions into a `(PulseStatus, string)` tuple — `PulseException` passes through, cancellation maps to `TIMEOUT`, and everything else maps to `INTERNAL_ERROR`.

## Thread Safety

All public APIs are thread-safe and designed for concurrent access:
- `ConcurrentDictionary` for connection and group storage
- `ReaderWriterLockSlim` for regex route registration
- Thread-safe cleanup timers
- Async/await throughout for proper synchronization

## Performance Tips

1. **Message size** — keep messages under 10 MiB (default limit, configurable).
2. **Groups** — remove connections from groups when no longer needed.
3. **Metadata** — limit connection metadata usage.
4. **Handlers** — keep handlers lightweight and async.
5. **Route shape** — prefer literal handles over patterns; patterns fall back to regex matching.

## Extension Points

### Custom Logger
```csharp
public class CustomLogger : IPulseLogger
{
    public void LogDebug(string message) { /* ... */ }
    public void LogInfo(string message) { /* ... */ }
    public void LogWarning(string message) { /* ... */ }
    public void LogError(string message, Exception? exception = null) { /* ... */ }
}
```

A ready-to-use `PulseConsoleLogger` is included; pass `enableDebugLogs: true` to surface debug-level entries.

### Custom Middleware
```csharp
dispatcher.Use(async (context, next) =>
{
    // Custom logic
    await next();
});
```

### Custom Serialization
`MsgPackSerdes` is the single serialization gateway. Replace or wrap it if you need a different wire format; the wire layout is `nonce(12) | MessagePack-encoded ciphertext | tag(16)`.

## Dependencies

- .NET 10.0 or later
- MessagePack 3.1.4
- NaCl.Core 2.0.6

## License

This project is licensed under the BSD 3-Clause License.
