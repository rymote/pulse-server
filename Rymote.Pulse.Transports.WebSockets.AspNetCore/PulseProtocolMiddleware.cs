using System.Net.WebSockets;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Rymote.Pulse.Core;
using Rymote.Pulse.Core.Logging;
using Rymote.Pulse.Core.Transport;
using Rymote.Pulse.Transports.Multiplexing;

namespace Rymote.Pulse.Transports.WebSockets.AspNetCore;

public static class PulseProtocolMiddleware
{
    public static IApplicationBuilder UsePulseProtocol(
        this IApplicationBuilder applicationBuilder,
        string websocketPath,
        PulseDispatcher pulseDispatcher,
        IPulseLogger pulseLogger,
        Action<PulseProtocolOptions>? configureOptionsAction = null)
    {
        ArgumentNullException.ThrowIfNull(applicationBuilder);
        ArgumentNullException.ThrowIfNull(pulseDispatcher);
        ArgumentNullException.ThrowIfNull(pulseLogger);

        PulseProtocolOptions options = new PulseProtocolOptions();
        configureOptionsAction?.Invoke(options);

        return applicationBuilder.Map(websocketPath, subApplication =>
        {
            subApplication.Use(async (HttpContext httpContext, Func<Task> nextDelegate) =>
            {
                if (!httpContext.WebSockets.IsWebSocketRequest)
                {
                    pulseLogger.LogWarning($"Rejected non-WebSocket request on path {websocketPath}");
                    httpContext.Response.StatusCode = StatusCodes.Status400BadRequest;
                    return;
                }

                WebSocket webSocket = await httpContext.WebSockets.AcceptWebSocketAsync();
                WebSocketByteStream byteStream = new WebSocketByteStream(
                    webSocket, options.InitialReceiveBufferSizeInBytes);

                Dictionary<string, string> queryParameters = httpContext.Request.Query.ToDictionary(
                    keyValuePair => keyValuePair.Key,
                    keyValuePair => keyValuePair.Value.ToString());

                Dictionary<string, object> initialMetadata = new Dictionary<string, object>
                {
                    ["websocket"] = webSocket,
                    ["http_context"] = httpContext,
                    ["ip_address"] = ResolveClientIpAddress(httpContext),
                    ["user_agent"] = httpContext.Request.Headers["User-Agent"].FirstOrDefault() ?? "Unknown",
                    ["origin"] = httpContext.Request.Headers["Origin"].FirstOrDefault() ?? "Unknown"
                };

                PulseStreamMultiplexerOptions multiplexerOptions = new PulseStreamMultiplexerOptions
                {
                    MaxFramePayloadSizeInBytes = options.MaxFramePayloadSizeInBytes,
                    MaxDatagramEnvelopeSizeInBytes = options.MaxDatagramEnvelopeSizeInBytes,
                    DatagramsEnabled = options.DatagramsEnabled
                };

                PulseStreamMultiplexer session = new PulseStreamMultiplexer(
                    byteStream,
                    isServerSide: true,
                    transportName: "websocket-aspnetcore",
                    logger: pulseLogger,
                    options: multiplexerOptions,
                    queryParameters: queryParameters,
                    initialMetadata: initialMetadata);

                session.Start();

                try
                {
                    await PulseSessionLifecycle.HandleAsync(
                        session,
                        pulseDispatcher,
                        pulseLogger,
                        httpContext.RequestAborted);
                }
                catch (Exception lifecycleException)
                {
                    pulseLogger.LogError(
                        $"[{session.SessionId}] Unhandled exception in Pulse protocol middleware",
                        lifecycleException);
                }
            });
        });
    }

    private static string ResolveClientIpAddress(HttpContext httpContext)
    {
        string? forwardedFor = httpContext.Request.Headers["X-Forwarded-For"].FirstOrDefault();
        if (!string.IsNullOrEmpty(forwardedFor))
            return forwardedFor.Split(',')[0].Trim();

        string? realIp = httpContext.Request.Headers["X-Real-IP"].FirstOrDefault();
        if (!string.IsNullOrEmpty(realIp))
            return realIp;

        return httpContext.Connection.RemoteIpAddress?.ToString() ?? "Unknown";
    }
}
