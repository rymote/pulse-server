<div align="center">
    <a href="https://github.com/rymote/pulse-server"><img src="https://github.com/rymote/pulse-server/blob/master/.github/rymote-pulse-server-cover.png" alt="rymote/pulse-server" /></a>
</div>
<br />

<div align="center">
  Rymote.Pulse - High-performance, real-time messaging framework for .NET
</div>

<div align="center">
  <sub>
    Brought to you by
    <a href="https://github.com/jovanivanovic">@jovanivanovic</a>,
    <a href="https://github.com/rymote">@rymote</a>
  </sub>
</div>

## Overview

Rymote.Pulse is a modern messaging framework designed to simplify real-time communication in distributed .NET applications. It provides a clean, strongly-typed API for handling request/response (RPC) and event-driven messaging over WebSockets.

## Features

- **High performance** — built on WebSockets with efficient binary serialization using MessagePack.
- **Authenticated encryption** — every payload is wrapped in a ChaCha20-Poly1305 AEAD frame.
- **Communication patterns** — strongly-typed RPC and event/broadcast messaging.
- **Middleware pipeline** — extensible middleware system for cross-cutting concerns.
- **Connection groups** — built-in support for broadcasting to connection groups.
- **Pluggable clustering** — optional clustering abstractions (`IPulseClusterStore`, `IPulseClusterMessaging`) for horizontal scaling.
- **Dependency injection** — first-class support for ASP.NET Core DI.
- **Attribute routing** — optional attribute-based handler registration via `Rymote.Pulse.Attributes`.
- **Type safety** — strongly-typed message contracts with compile-time safety.
- **Memory efficient** — ArrayPool-based buffer management and automatic cleanup.

## Projects

### [Rymote.Pulse.Core](./Rymote.Pulse.Core/README.md)
The core library containing the fundamental messaging infrastructure, connection management, and protocol implementation.

### [Rymote.Pulse.AspNet](./Rymote.Pulse.AspNet/README.md)
ASP.NET Core integration providing middleware for WebSocket handling and seamless integration with the ASP.NET Core pipeline.

### Rymote.Pulse.Attributes
Optional attribute-based handler registration. Scan an assembly and wire up RPC, event, connect/disconnect, and metadata-changed handlers automatically.

## Installation

```bash
# Core package
dotnet add package Rymote.Pulse.Core

# ASP.NET Core integration
dotnet add package Rymote.Pulse.AspNet

# Optional attribute-based handler registration
dotnet add package Rymote.Pulse.Attributes
```

## Examples

### Server setup

```csharp
var builder = WebApplication.CreateBuilder(args);

// Add Pulse services
builder.Services.AddSingleton<IPulseLogger>(new PulseConsoleLogger(enableDebugLogs: false));
builder.Services.AddSingleton<PulseConnectionManager>();
builder.Services.AddSingleton<PulseDispatcher>(serviceProvider =>
{
    var connectionManager = serviceProvider.GetRequiredService<PulseConnectionManager>();
    var logger = serviceProvider.GetRequiredService<IPulseLogger>();
    return new PulseDispatcher(connectionManager, logger);
});

var app = builder.Build();

// Configure Pulse dispatcher
var dispatcher = app.Services.GetRequiredService<PulseDispatcher>();
var logger = app.Services.GetRequiredService<IPulseLogger>();

// Map RPC endpoint
dispatcher.MapRpc<CalculateRequest, CalculateResponse>("calculator.add",
    async (request, context) =>
    {
        return new CalculateResponse
        {
            Result = request.A + request.B
        };
    });

// Use Pulse WebSocket middleware (options callback is optional)
app.UsePulseProtocol("/pulse", dispatcher, logger, options =>
{
    options.BufferSizeInBytes = 4 * 1024;
    options.MaxMessageSizeInBytes = 10 * 1024 * 1024;
});

app.Run();
```

### RPC (request/response)

```csharp
dispatcher.MapRpc<GetUserRequest, UserResponse>("users.get",
    async (request, context) =>
    {
        var user = await userService.GetUserAsync(request.UserId);
        return new UserResponse { Id = user.Id, Name = user.Name };
    });
```

### Route parameters

Handles support brace-style parameters that are extracted from the incoming handle:

```csharp
dispatcher.MapRpc<GetItemResponse>("inventory.{itemId}.get",
    async context =>
    {
        var itemId = context.GetRequiredParameter<string>("itemId");
        return await inventory.GetAsync(itemId);
    });
```

### Events and broadcasting

```csharp
// Server - Send to the current connection
await context.SendEventAsync("user.notification",
    new NotificationEvent { Message = "Hello!" });

// Server - Send to a specific connection
await context.SendEventAsync(otherConnection, "user.notification",
    new NotificationEvent { Message = "Hello!" });

// Server - Broadcast to a group
var group = context.ConnectionManager.GetOrCreateGroup("premium-users");
await context.ConnectionManager.AddToGroupAsync("premium-users", context.Connection);
await context.SendEventAsync(group, "feature.update",
    new FeatureUpdateEvent { Feature = "New Dashboard" });
```

## Architecture

```
┌─────────────────┐     ┌──────────────────┐     ┌─────────────────┐
│   Pulse Client  │────▶│  WebSocket Layer │────▶│  Pulse Server   │
└─────────────────┘     └──────────────────┘     └─────────────────┘
                                                           │
                                                           ▼
                              ┌─────────────────────────────────────────┐
                              │          Middleware Pipeline            │
                              │  ┌─────────┐  ┌─────────┐  ┌────────┐   │
                              │  │ Logging │─▶│  Auth   │─▶│ Custom │   │
                              │  └─────────┘  └─────────┘  └────────┘   │
                              └─────────────────────────────────────────┘
                                                           │
                                                           ▼
                                                  ┌─────────────────┐
                                                  │    Dispatcher   │
                                                  │  ┌───────────┐  │
                                                  │  │ Handlers  │  │
                                                  │  └───────────┘  │
                                                  └─────────────────┘
```

## Memory management

Rymote.Pulse includes several memory optimization features:

- **ArrayPool buffer management** — receive buffers and message-assembly buffers are pooled via `ArrayPool<byte>.Shared`.
- **Pooled serialization writers** — `MsgPackSerdes` reuses `ArrayBufferWriter<byte>` instances sized to processor count.
- **Connection metadata limits** — each connection caps at 100 metadata entries.
- **Empty group removal** — empty groups are removed; each group also runs a periodic cleanup of closed/failed members every 30 seconds.

## Configuration

### Logging

Implement the `IPulseLogger` interface to integrate with your logging framework:

```csharp
public class SerilogPulseLogger : IPulseLogger
{
    private readonly ILogger _logger;

    public void LogDebug(string message) => _logger.Debug(message);
    public void LogInfo(string message) => _logger.Information(message);
    public void LogWarning(string message) => _logger.Warning(message);
    public void LogError(string message, Exception? exception = null) =>
        _logger.Error(exception, message);
}
```

A `PulseConsoleLogger` is shipped out of the box. Pass `enableDebugLogs: true` to surface debug messages.

### Clustering

Pulse ships abstractions for clustering but does not bundle a transport implementation. Provide your own (Redis, NATS, etc.) by implementing `IPulseClusterStore` — and optionally `IPulseClusterMessaging` — and wiring it into the connection manager:

```csharp
services.AddSingleton<IPulseClusterStore, RedisPulseClusterStore>();

services.AddSingleton<PulseConnectionManager>(serviceProvider =>
{
    var clusterStore = serviceProvider.GetRequiredService<IPulseClusterStore>();
    var logger = serviceProvider.GetRequiredService<IPulseLogger>();
    return new PulseConnectionManager(clusterStore, nodeId: "node-1", logger);
});
```

## Performance considerations

- **Message size** — default maximum message size is 10 MiB (configurable via `PulseProtocolOptions.MaxMessageSizeInBytes`).
- **Buffer size** — default receive buffer is 4 KiB (configurable via `PulseProtocolOptions.BufferSizeInBytes`).
- **Group cleanup** — empty groups are removed; each group purges closed/failed connections every 30 seconds.
- **Metadata limits** — maximum 100 metadata entries per connection.

## Contributing

We welcome contributions! Please see our [Contributing Guide](./CONTRIBUTING.md) for details.

## Support the project

If Pulse has helped you ship faster, please consider supporting ongoing development:

- [Patreon](https://www.patreon.com/rymote)
- [Open Collective](https://opencollective.com/rymote)

## License

This project is licensed under the BSD 3-Clause License — see [LICENSE.md](./LICENSE.md) for details.

## Support

- Issues: [GitHub Issues](https://github.com/rymote/pulse-server/issues)
- Discussions: [GitHub Discussions](https://github.com/rymote/pulse-server/discussions)
