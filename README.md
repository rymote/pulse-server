<div align="center">
    <a href="https://github.com/rymote/pulse-server"><img src="https://github.com/rymote/pulse-server/blob/master/.github/rymote-pulse-server-cover.png" alt="rymote/bytebinder" /></a>
</div>
<br />

<div align="center">
  Pulse Server - High-performance, real-time messaging framework for .NET
</div>

<div align="center">
  <sub>
    Brought to you by 
    <a href="https://github.com/jovanivanovic">@jovanivanovic</a>,
    <a href="https://github.com/rymote">@rymote</a>
  </sub>
</div>

## Overview

Rymote.Pulse is a modern messaging framework designed to simplify real-time communication in distributed .NET applications. It provides a clean, strongly-typed API for handling various messaging patterns including request/response, server streaming, client streaming, and event broadcasting.

## Key Features

- 🚀 **High Performance**: Built on WebSockets with efficient binary serialization using MessagePack
- 📡 **Multiple Communication Patterns**: RPC, server streaming, client streaming, and event-driven messaging
- 🔌 **Middleware Pipeline**: Extensible middleware system for cross-cutting concerns
- 👥 **Connection Groups**: Built-in support for broadcasting to connection groups
- 🌐 **Clustering Support**: Optional Redis-based clustering for horizontal scaling
- 💉 **Dependency Injection**: First-class support for ASP.NET Core DI
- 🔧 **MediatR Integration**: Optional integration with MediatR for CQRS patterns
- 🛡️ **Type Safety**: Strongly-typed message contracts with compile-time safety
- 🎯 **Memory Efficient**: ArrayPool-based buffer management and automatic cleanup

## Projects

### [Rymote.Pulse.Core](./Rymote.Pulse.Core/README.md)
The core library containing the fundamental messaging infrastructure, connection management, and protocol implementation.

### [Rymote.Pulse.AspNet](./Rymote.Pulse.AspNet/README.md)
ASP.NET Core integration providing middleware for WebSocket handling and seamless integration with the ASP.NET Core pipeline.

### [Rymote.Pulse.MediatR](./Rymote.Pulse.MediatR/README.md)
Optional MediatR integration enabling CQRS patterns and simplified handler registration.

## Quick Start

### Installation

```bash
# Core package
dotnet add package Rymote.Pulse.Core

# ASP.NET Core integration
dotnet add package Rymote.Pulse.AspNet

# Optional MediatR integration
dotnet add package Rymote.Pulse.MediatR
```

### Basic Usage

#### Server Setup

```csharp
var builder = WebApplication.CreateBuilder(args);

// Add Pulse services
builder.Services.AddSingleton<PulseConnectionManager>();
builder.Services.AddSingleton<PulseDispatcher>(sp =>
{
    var connectionManager = sp.GetRequiredService<PulseConnectionManager>();
    var logger = sp.GetRequiredService<IPulseLogger>();
    return new PulseDispatcher(connectionManager, logger);
});

var app = builder.Build();

// Configure Pulse dispatcher
var dispatcher = app.Services.GetRequiredService<PulseDispatcher>();

// Map RPC endpoint
dispatcher.MapRpc<CalculateRequest, CalculateResponse>("calculator.add",
    async (request, context) =>
    {
        return new CalculateResponse 
        { 
            Result = request.A + request.B 
        };
    });

// Use Pulse WebSocket middleware
app.UsePulseProtocol("/pulse", dispatcher, logger);

app.Run();
```

## Communication Patterns

### 1. RPC (Request/Response)

```csharp
dispatcher.MapRpc<GetUserRequest, UserResponse>("users.get",
    async (request, context) =>
    {
        var user = await userService.GetUserAsync(request.UserId);
        return new UserResponse { Id = user.Id, Name = user.Name };
    });
```

### 2. Server Streaming

```csharp
dispatcher.MapRpcStream<SubscribeRequest, PriceUpdate>("prices.subscribe",
    async function* (request, context)
    {
        await foreach (var price in priceService.GetPriceUpdates(request.Symbol))
        {
            yield return new PriceUpdate { Symbol = request.Symbol, Price = price };
        }
    });
```

### 3. Events & Broadcasting

```csharp
// Server - Send to specific connection
await context.SendEventAsync(connection, "user.notification", 
    new NotificationEvent { Message = "Hello!" });

// Server - Broadcast to group
var group = context.ConnectionManager.GetOrCreateGroup("premium-users");
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
                              │          Middleware Pipeline           │
                              │  ┌─────────┐  ┌─────────┐  ┌────────┐ │
                              │  │ Logging │─▶│  Auth   │─▶│ Custom │ │
                              │  └─────────┘  └─────────┘  └────────┘ │
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

## Memory Management

Rymote.Pulse includes several memory optimization features:

- **ArrayPool Buffer Management**: Efficient buffer pooling for message processing
- **Automatic Stream Cleanup**: Abandoned streams are automatically cleaned up after timeout
- **Connection Metadata Limits**: Prevents unbounded metadata growth
- **Empty Group Removal**: Automatically removes empty connection groups

## Configuration

### Logging

Implement the `IPulseLogger` interface to integrate with your logging framework:

```csharp
public class SerilogPulseLogger : IPulseLogger
{
    private readonly ILogger _logger;
    
    public void LogInfo(string message) => _logger.Information(message);
    public void LogWarning(string message) => _logger.Warning(message);
    public void LogError(string message, Exception? exception = null) => 
        _logger.Error(exception, message);
}
```

### Clustering

Enable Redis-based clustering for horizontal scaling:

```csharp
services.AddSingleton<IClusterStore>(sp =>
{
    var redis = ConnectionMultiplexer.Connect("localhost:6379");
    return new RedisClusterStore(redis);
});

services.AddSingleton<PulseConnectionManager>(sp =>
{
    var clusterStore = sp.GetRequiredService<IClusterStore>();
    var logger = sp.GetRequiredService<IPulseLogger>();
    return new PulseConnectionManager(clusterStore, nodeId: "node-1", logger);
});
```

## Performance Considerations

- **Message Size**: Default maximum message size is 10MB (configurable)
- **Buffer Size**: Adaptive buffer sizing between 1KB and 64KB
- **Cleanup Intervals**: Abandoned streams cleaned up after 5 minutes
- **Metadata Limits**: Maximum 100 metadata entries per connection

## Contributing

We welcome contributions! Please see our [Contributing Guide](./CONTRIBUTING.md) for details.

## License

This project is licensed under the MIT License - see the [LICENSE](./LICENSE) file for details.

## Support

- 📧 Email: support@rymote.com
- 🐛 Issues: [GitHub Issues](https://github.com/rymote/pulse/issues)
- 💬 Discussions: [GitHub Discussions](https://github.com/rymote/pulse/discussions) 