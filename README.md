<div align="center">
    <a href="https://github.com/rymote/pulse-server"><img src="https://github.com/rymote/pulse-server/blob/master/.github/rymote-pulse-server-cover.png" alt="rymote/pulse-server" /></a>
</div>
<br />

<div align="center">
  Rymote.Pulse — High-performance, real-time messaging framework for .NET
</div>

<div align="center">
  <sub>
    Brought to you by
    <a href="https://github.com/jovanivanovic">@jovanivanovic</a>,
    <a href="https://github.com/rymote">@rymote</a>
  </sub>
</div>

## Overview

Rymote.Pulse is a real-time messaging framework for .NET. v3 introduces a stream-per-RPC protocol with pluggable transports (WebSocket, raw TCP, WebTransport over HTTP/3), so the same dispatcher and handlers can serve browser, mobile, and service-to-service clients over whichever transport works in each environment.

## Features

- **Stream-per-RPC protocol** — each RPC owns its own bidirectional stream; events use unidirectional streams; lossy events use datagrams. No more correlation-id multiplexing.
- **Pluggable transports** — WebSocket (HttpListener or ASP.NET Core), raw TCP (with optional TLS), and WebTransport over HTTP/3. Mix on a single host.
- **Stream multiplexer** — single-channel transports (WS, TCP) carry many concurrent streams via an HTTP/2-style frame layer in `Rymote.Pulse.Transports.Multiplexing`.
- **Authenticated encryption** — every envelope is wrapped in a ChaCha20-Poly1305 AEAD frame.
- **Generic-host DI** — transport-agnostic `services.AddPulse()` returns an `IPulseBuilder`; each transport package extends it.
- **Server streaming** — `MapRpcStream` handlers return `IAsyncEnumerable<TResponse>` on a bidi stream.
- **Datagram events** — `PulseKind.DATAGRAM_EVENT` for lossy fire-and-forget over WT (datagram exposure on Kestrel WT is a .NET-10 platform gap; multiplexed transports emulate via the same op-code over TCP).
- **Middleware pipeline** — extensible middleware for cross-cutting concerns (auth, logging, concurrency limits).
- **Connection groups** — broadcast to named groups across all attached transports.
- **Pluggable clustering** — `IPulseClusterStore` / `IPulseClusterMessaging` abstractions for horizontal scaling.
- **Attribute routing** — optional reflection-based handler registration via `Rymote.Pulse.Attributes`.

## Packages

### [Rymote.Pulse.Core](./Rymote.Pulse.Core/README.md)
Dispatcher, envelope, transport contracts (`IPulseSession` / `IPulseStream` / `IPulseDatagramChannel`), session pipeline/lifecycle, connection manager, groups, and serialization.

### Rymote.Pulse.Hosting
Transport-agnostic generic-host DI. `services.AddPulse()` returns an `IPulseBuilder` that each transport package extends with its own `AddXxxTransport(...)`.

### Rymote.Pulse.Attributes
Optional attribute-based handler registration. Scans an assembly and wires up RPC, event, connect/disconnect, and metadata-changed handlers.

### Rymote.Pulse.Transports.Multiplexing
HTTP/2-style stream multiplexer shared by WebSocket and Raw TCP transports. Carries many virtual streams + a datagram channel over one byte channel.

### Rymote.Pulse.Transports.WebSockets.HttpListener
Standalone WebSocket transport based on `System.Net.HttpListener` for generic-host applications (no ASP.NET dependency).

### Rymote.Pulse.Transports.WebSockets.AspNetCore
WebSocket transport that plugs into an existing ASP.NET Core pipeline via `app.UsePulseProtocol(...)`.

### Rymote.Pulse.Transports.RawTcp
Raw TCP transport with optional TLS (`SslStream`) and mTLS support. For service-to-service deployments where HTTP framing isn't needed.

### Rymote.Pulse.Transports.WebTransport.AspNetCore
WebTransport-over-HTTP/3 transport via Kestrel + the experimental `IHttpWebTransportFeature`. For browser clients that need per-stream flow control.

### Rymote.Pulse.Client
.NET client SDK with transport fallback negotiation. Same wire as the server; tries each registered transport in order.

### Rymote.Pulse.Client.Transports.WebSockets / .RawTcp / .WebTransport
Client-side transport implementations matching the server packages.

## Installation

```bash
# Core
dotnet add package Rymote.Pulse.Core

# Generic-host DI
dotnet add package Rymote.Pulse.Hosting

# Pick one or more server transports
dotnet add package Rymote.Pulse.Transports.WebSockets.HttpListener
dotnet add package Rymote.Pulse.Transports.WebSockets.AspNetCore
dotnet add package Rymote.Pulse.Transports.RawTcp
dotnet add package Rymote.Pulse.Transports.WebTransport.AspNetCore

# .NET client
dotnet add package Rymote.Pulse.Client
dotnet add package Rymote.Pulse.Client.Transports.WebSockets
dotnet add package Rymote.Pulse.Client.Transports.RawTcp
dotnet add package Rymote.Pulse.Client.Transports.WebTransport

# Optional attribute-based handler registration
dotnet add package Rymote.Pulse.Attributes
```

## Examples

### Server — generic host with WebSocket + raw TCP

```csharp
HostApplicationBuilder builder = Host.CreateApplicationBuilder(args);

builder.Services
    .AddPulse()
    .AddWebSocketHttpListenerTransport(options =>
    {
        options.Prefixes.Add("http://+:8080/pulse/");
    })
    .AddRawTcpTransport(options =>
    {
        options.Endpoint = new IPEndPoint(IPAddress.Any, 9000);
    });

IHost host = builder.Build();

PulseDispatcher dispatcher = host.Services.GetRequiredService<PulseDispatcher>();

dispatcher.MapRpc<CalculateRequest, CalculateResponse>("calculator.add",
    async (request, context) => new CalculateResponse { Result = request.A + request.B });

dispatcher.MapRpcStream<PriceSubscription, PriceUpdate>("prices.subscribe",
    async (request, context) =>
    {
        await foreach (PriceUpdate update in priceFeed.WatchAsync(request.Symbol, context.CancellationToken))
            yield return update;
    });

dispatcher.MapEvent<NotificationEvent>("user.notification",
    async (notification, context) => await notifications.LogAsync(notification));

await host.RunAsync();
```

### Server — ASP.NET Core integration

```csharp
WebApplicationBuilder builder = WebApplication.CreateBuilder(args);
builder.Services.AddSingleton<IPulseLogger>(new PulseConsoleLogger(enableDebugLogs: false));
builder.Services.AddSingleton<PulseConnectionManager>();
builder.Services.AddSingleton<PulseDispatcher>(serviceProvider =>
    new PulseDispatcher(serviceProvider.GetRequiredService<PulseConnectionManager>(),
                       serviceProvider.GetRequiredService<IPulseLogger>()));

WebApplication app = builder.Build();
PulseDispatcher dispatcher = app.Services.GetRequiredService<PulseDispatcher>();
IPulseLogger logger = app.Services.GetRequiredService<IPulseLogger>();

dispatcher.MapRpc<EchoRequest, EchoResponse>("echo",
    async (request, context) => new EchoResponse { Message = request.Message });

app.UsePulseProtocol("/pulse", dispatcher, logger);

app.Run();
```

### .NET client — transport fallback

```csharp
PulseClient client = new PulseClient(new PulseClientOptions
{
    Endpoint = new Uri("https://pulse.example.com/pulse"),
    Transports = new IPulseClientTransport[]
    {
        new WebTransportClientTransport(),    // tried first
        new WebSocketClientTransport(),       // fallback if WT can't connect
    },
    AuthToken = "session-token",
    AutoReconnect = true,
});

await client.ConnectAsync();

EchoResponse response = await client.InvokeAsync<EchoRequest, EchoResponse>(
    "echo", new EchoRequest { Message = "Hello" });

await foreach (PriceUpdate update in client.InvokeStreamAsync<PriceSubscription, PriceUpdate>(
    "prices.subscribe", new PriceSubscription { Symbol = "AAPL" }))
{
    Console.WriteLine(update);
}

await client.SendEventAsync("user.heartbeat", new HeartbeatEvent { Timestamp = DateTime.UtcNow });

client.On<NotificationEvent>("user.notification", async (notification, context) =>
{
    Console.WriteLine($"From server: {notification.Message}");
});
```

### Route parameters

Handles support brace-style parameters extracted from the incoming handle:

```csharp
dispatcher.MapRpc<GetItemResponse>("inventory.{itemId}.get",
    async context =>
    {
        string itemId = context.GetRequiredParameter<string>("itemId");
        return await inventory.GetAsync(itemId);
    });
```

### Events and broadcasting

```csharp
// Send to the current connection
await context.SendEventAsync("user.notification",
    new NotificationEvent { Message = "Hello" });

// Broadcast to a group
PulseGroup group = context.ConnectionManager.GetOrCreateGroup("premium-users");
await context.ConnectionManager.AddToGroupAsync("premium-users", context.Connection);
await context.SendEventAsync(group, "feature.update",
    new FeatureUpdateEvent { Feature = "New Dashboard" });

// Lossy datagram event (transport must support datagrams)
await context.SendEventAsync(context.Connection, "user.cursor",
    new CursorPositionEvent { X = 123, Y = 456 },
    deliveryMode: PulseDeliveryMode.Datagram);
```

## Architecture

```
┌──────────────┐                  ┌──────────────────────────────────┐
│ Pulse Client │ ── transport ──▶ │ Transport (WT / WS / Raw TCP)    │
└──────────────┘                  │  ─ native streams (WT)           │
                                  │  ─ multiplexer over byte channel │
                                  └──────────────────┬───────────────┘
                                                     ▼
                                  ┌──────────────────────────────────┐
                                  │ PulseSessionLifecycle            │
                                  │  OnConnect → pipeline → cleanup  │
                                  └──────────────────┬───────────────┘
                                                     ▼
                                  ┌──────────────────────────────────┐
                                  │ Middleware pipeline (per stream) │
                                  └──────────────────┬───────────────┘
                                                     ▼
                                  ┌──────────────────────────────────┐
                                  │ PulseDispatcher                  │
                                  │  Map(Rpc|RpcStream|Event)        │
                                  └──────────────────────────────────┘
```

## Memory management

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
    public void LogError(string message, Exception? exception = null) => _logger.Error(exception, message);
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

- **Per-stream concurrency** — each accepted stream runs its own task; one slow handler doesn't block sibling streams.
- **Max envelope size** — defaults to 10 MiB per envelope; configurable on each transport's options.
- **Stream multiplexer buffering** — single-channel transports share the underlying socket's send buffer; a slow consumer on one virtual stream applies head-of-line back-pressure to siblings. WebTransport sidesteps this with native QUIC per-stream flow control.
- **Metadata limits** — maximum 100 metadata entries per connection.

## v2 → v3 migration

v3 is a **hard cut**. The wire format is incompatible with v2 — clients and servers must upgrade together. Key changes:

- `ClientCorrelationId` removed from `PulseEnvelope`; stream identity is the correlation.
- `PulseKind` now has four values: `RPC`, `RPC_STREAM`, `EVENT`, `DATAGRAM_EVENT`.
- The `Rymote.Pulse.AspNet` package was renamed to `Rymote.Pulse.Transports.WebSockets.AspNetCore`. `app.UsePulseProtocol(...)` still works with the same signature.
- The server-side abstraction `IPulseTransportConnection` (v2) is replaced by `IPulseSession` + `IPulseStream` + `IPulseDatagramChannel`.

## Contributing

Please see our [Contributing Guide](./CONTRIBUTING.md) for details.

## Support the project

If Pulse has helped you ship faster, please consider supporting ongoing development:

- [Patreon](https://www.patreon.com/rymote)
- [Open Collective](https://opencollective.com/rymote)

## License

This project is licensed under the BSD 3-Clause License — see [LICENSE.md](./LICENSE.md) for details.

## Support

- Issues: [GitHub Issues](https://github.com/rymote/pulse-server/issues)
- Discussions: [GitHub Discussions](https://github.com/rymote/pulse-server/discussions)
