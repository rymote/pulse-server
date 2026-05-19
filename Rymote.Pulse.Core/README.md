# Rymote.Pulse.Core

The core of the Rymote.Pulse v3 framework: dispatcher, envelope, transport contracts, session pipeline + lifecycle, connection manager, groups, and serialization. Transport implementations and the .NET client live in their own packages and ride on the contracts defined here.

## Overview

Rymote.Pulse.Core contains:
- The `PulseDispatcher` and middleware pipeline
- Transport contracts: `IPulseTransport`, `IPulseSession`, `IPulseStream`, `IPulseDatagramChannel`
- Per-session orchestration: `PulseSessionLifecycle`, `PulseSessionPipeline`
- Connection identity + groups: `PulseConnection`, `PulseConnectionManager`, `PulseGroup`
- Wire envelope + MessagePack + ChaCha20-Poly1305 AEAD framing (`MsgPackSerdes`)
- Optional clustering abstractions (`IPulseClusterStore`, `IPulseClusterMessaging`)

## Installation

```bash
dotnet add package Rymote.Pulse.Core
```

## Key Components

### PulseDispatcher

Routes incoming envelopes to handlers based on kind, handle, and version.

```csharp
PulseDispatcher dispatcher = new PulseDispatcher(connectionManager, logger);

// Request/response RPC
dispatcher.MapRpc<RequestType, ResponseType>("handle.name",
    async (request, context) => new ResponseType { /* ... */ });

// Response-only RPC (useful for handles with route parameters)
dispatcher.MapRpc<ResponseType>("items.{itemId}.get",
    async context =>
    {
        string itemId = context.GetRequiredParameter<string>("itemId");
        return new ResponseType { /* ... */ };
    });

// Server-streaming RPC — handler returns IAsyncEnumerable<TResponse>
dispatcher.MapRpcStream<SubscribeRequest, PriceUpdate>("prices.subscribe",
    async (request, context) =>
    {
        await foreach (PriceUpdate update in priceFeed.WatchAsync(request.Symbol, context.CancellationToken))
            yield return update;
    });

// Fire-and-forget event (covers both reliable EVENT and lossy DATAGRAM_EVENT)
dispatcher.MapEvent<EventType>("event.name",
    async (eventData, context) => { /* ... */ });
```

A version string (default `"v1"`) can be passed to any `Map*` call. Handles containing `{name}`, `*`, or `[` are stored as compiled regex routes; literal handles use the fast lookup dictionary.

### PulseConnection / PulseConnectionManager

`PulseConnection` is the per-client identity. It wraps an `IPulseSession` from the active transport (browser WebSocket, raw TCP, WebTransport, etc.) and exposes group/metadata operations agnostic to transport.

```csharp
PulseConnectionManager connectionManager = new PulseConnectionManager(clusterStore, nodeId, logger);

// Add a connection (called by transports during session lifecycle — rarely from user code)
PulseConnection connection = await connectionManager.AddConnectionAsync(session);

// Group membership
await connectionManager.AddToGroupAsync("group-name", connection);
await connectionManager.RemoveFromGroupAsync("group-name", connectionId);

// Lookup
PulseConnection? found = connectionManager.GetConnection(connectionId);
```

All constructor parameters are optional: `IPulseClusterStore?`, `string? nodeId` (defaults to `Environment.MachineName`), and `IPulseLogger?`.

### PulseContext

Provided to every handler. Exposes the current request, the connection, route parameters, a cancellation token tied to the stream lifetime, and helpers to push events.

```csharp
public async Task<Response> HandleRequest(Request request, PulseContext context)
{
    string connectionId = context.Connection.ConnectionId;

    // Route parameters (when the handle contains placeholders like "users.{id}")
    string? userId = context.GetParameter<string>("id");

    // Send a server-pushed event to the caller, or to any connection, or to a group
    await context.SendEventAsync("user.notification",
        new NotificationEvent { Message = "Hello" });

    await context.SendEventAsync(otherConnection, "user.notification",
        new NotificationEvent { Message = "Direct" });

    PulseGroup group = context.ConnectionManager.GetOrCreateGroup("premium-users");
    await context.SendEventAsync(group, "feature.update",
        new FeatureUpdateEvent { Feature = "New Dashboard" });

    // Lossy datagram (transport must support datagrams)
    await context.SendEventAsync("user.cursor",
        new CursorPositionEvent { X = 123, Y = 456 },
        deliveryMode: PulseDeliveryMode.Datagram);

    // Cancellation tied to this stream
    if (context.CancellationToken.IsCancellationRequested) return /* ... */;

    return new Response { /* ... */ };
}
```

### Transport contracts

```csharp
public interface IPulseTransport
{
    string Name { get; }
    IAsyncEnumerable<IPulseSession> AcceptSessionsAsync(CancellationToken cancellationToken);
}

public interface IPulseSession : IAsyncDisposable
{
    string SessionId { get; }
    string TransportName { get; }
    bool IsOpen { get; }
    IReadOnlyDictionary<string, string> QueryParameters { get; }
    IReadOnlyDictionary<string, object> InitialMetadata { get; }
    IPulseDatagramChannel? Datagrams { get; }

    ValueTask<IPulseStream?> AcceptStreamAsync(CancellationToken cancellationToken);
    ValueTask<IPulseStream> OpenStreamAsync(PulseStreamDirection direction, CancellationToken cancellationToken);
    ValueTask CloseAsync(int reasonCode, TimeSpan drainTimeout, CancellationToken cancellationToken);
}

public interface IPulseStream : IAsyncDisposable
{
    long StreamId { get; }
    PulseStreamDirection Direction { get; }
    bool IsClosed { get; }

    ValueTask<ReadOnlyMemory<byte>?> ReadEnvelopeAsync(CancellationToken cancellationToken);
    ValueTask WriteEnvelopeAsync(ReadOnlyMemory<byte> envelopeFrame, CancellationToken cancellationToken);
    ValueTask CompleteWritesAsync(CancellationToken cancellationToken);
    ValueTask AbortAsync(int reasonCode, CancellationToken cancellationToken);
}

public interface IPulseDatagramChannel
{
    int MaxDatagramEnvelopeSizeInBytes { get; }
    ValueTask<ReadOnlyMemory<byte>?> ReceiveDatagramAsync(CancellationToken cancellationToken);
    ValueTask SendDatagramAsync(ReadOnlyMemory<byte> envelopeFrame, CancellationToken cancellationToken);
}
```

Implementations live in the `Rymote.Pulse.Transports.*` packages. `PulseSessionLifecycle.HandleAsync(session, dispatcher, logger, ct)` is the shared entry point that every transport hosted service calls per accepted session.

### Middleware Pipeline

Cross-cutting concerns sit between the dispatcher and the handler.

```csharp
public class AuthenticationMiddleware
{
    public async Task InvokeAsync(PulseContext context, Func<Task> next)
    {
        string? token = context.UntypedRequest.AuthToken;
        if (!IsValidToken(token))
            throw new PulseException(PulseStatus.UNAUTHORIZED, "Invalid token");

        await next();
    }
}

dispatcher.Use(async (context, next) =>
{
    AuthenticationMiddleware middleware = new AuthenticationMiddleware();
    await middleware.InvokeAsync(context, next);
});
```

A built-in `ConcurrencyMiddleware` is provided for capping in-flight request count with a wait timeout that surfaces as `PulseStatus.TIMEOUT`.

## Message Types

### PulseEnvelope (v3)

```csharp
[MessagePackObject]
public class PulseEnvelope<T>
{
    [Key(0)] public string? Id { get; set; }
    [Key(1)] public string Handle { get; set; }
    [Key(2)] public T Body { get; set; }
    [Key(3)] public string? AuthToken { get; set; }
    [Key(4)] public PulseKind Kind { get; set; }
    [Key(5)] public string Version { get; set; }
    [Key(6)] public PulseStatus? Status { get; set; }
    [Key(7)] public string? Error { get; set; }
}
```

`ClientCorrelationId` from v2 is removed — in v3 the stream identity *is* the correlation.

### PulseKind

| Value | Meaning |
|---|---|
| `RPC = 0` | Request → response on one bidirectional stream |
| `RPC_STREAM = 1` | Request → N responses on one bidirectional stream (server streaming) |
| `EVENT = 2` | Fire-and-forget event on a unidirectional stream (reliable) |
| `DATAGRAM_EVENT = 3` | Fire-and-forget event on the datagram channel (lossy) |

### PulseStatus

| Value | Meaning |
|---|---|
| `OK` | Success |
| `BAD_REQUEST` | Client error |
| `UNAUTHORIZED` | Authentication required |
| `NOT_FOUND` | Handler not found |
| `INTERNAL_ERROR` | Server error |
| `TIMEOUT` | Operation timed out |
| `UNSUPPORTED` | Operation not supported |
| `CONFLICT` | Conflicting state |

## Serialization

Each envelope is encoded with MessagePack and then wrapped in a ChaCha20-Poly1305 AEAD frame (`nonce(12) | ciphertext | tag(16)`).

```csharp
byte[] data = MsgPackSerdes.Serialize(envelope);
PulseEnvelope<MyType> roundTripped = MsgPackSerdes.Deserialize<PulseEnvelope<MyType>>(data);
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

A `PulseClusterMessage` envelope (with `PulseClusterMessageType` of `GROUP_BROADCAST`, `DIRECT_MESSAGE`, or `HEALTH_CHECK`) is a building block for inter-node payloads.

## Memory Management

- **ArrayPool integration** — pooled receive and message-assembly buffers reduce GC pressure on the hot path.
- **Pooled serialization writers** — `MsgPackSerdes` reuses `ArrayBufferWriter<byte>` instances sized to processor count.
- **Automatic cleanup** — empty groups are removed when their last member leaves; each `PulseGroup` runs a periodic sweep every 30 seconds to prune closed/failed connections.
- **Metadata caps** — connection metadata is capped at 100 entries per connection.

## Error Handling

```csharp
try
{
    await dispatcher.ProcessStreamAsync(stream, connection, cancellationToken);
}
catch (PulseException pulseException)
{
    logger.LogError($"Pulse error: {pulseException.Status} — {pulseException.Message}");
}
catch (Exception exception)
{
    logger.LogError("Unexpected error", exception);
}
```

`ErrorMapper.MapException` translates known exceptions to `(PulseStatus, string)` — `PulseException` passes through, cancellation maps to `TIMEOUT`, everything else maps to `INTERNAL_ERROR`.

## Thread Safety

All public APIs are designed for concurrent access:
- `ConcurrentDictionary` for connection and group storage
- `ReaderWriterLockSlim` for regex route registration
- Per-stream task isolation in the dispatcher
- Async/await throughout

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

A ready-to-use `PulseConsoleLogger` is included; pass `enableDebugLogs: true` to surface debug entries.

### Custom Transport

Implement `IPulseTransport` + `IPulseSession` + `IPulseStream`, optionally `IPulseDatagramChannel`. See `Rymote.Pulse.Transports.RawTcp` for the smallest reference implementation.

### Custom Middleware

```csharp
dispatcher.Use(async (context, next) =>
{
    // Custom logic before
    await next();
    // Custom logic after
});
```

## Dependencies

- .NET 10.0 or later
- MessagePack 3.1.4
- NaCl.Core 2.0.6

## License

BSD 3-Clause License — see `LICENSE.md` at the repo root.
