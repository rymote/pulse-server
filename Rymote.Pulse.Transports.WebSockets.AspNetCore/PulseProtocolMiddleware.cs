using System.Net.WebSockets;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Rymote.Pulse.Core;
using Rymote.Pulse.Core.Logging;
using Rymote.Pulse.Core.Transport;

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

        PulseProtocolOptions pulseProtocolOptions = new PulseProtocolOptions();
        configureOptionsAction?.Invoke(pulseProtocolOptions);

        return applicationBuilder.Map(websocketPath, subApplication =>
        {
            subApplication.Use(async (HttpContext httpContext, Func<Task> nextDelegate) =>
            {
                if (!httpContext.WebSockets.IsWebSocketRequest)
                {
                    pulseLogger.LogWarning(
                        $"Rejected non-WebSocket request on path {websocketPath}");
                    httpContext.Response.StatusCode = StatusCodes.Status400BadRequest;
                    return;
                }

                string connectionId = Guid.NewGuid().ToString();
                WebSocket webSocket = await httpContext.WebSockets.AcceptWebSocketAsync();

                WebSocketAspNetCoreConnection transportConnection = new WebSocketAspNetCoreConnection(
                    connectionId,
                    webSocket,
                    httpContext,
                    pulseProtocolOptions.BufferSizeInBytes,
                    pulseProtocolOptions.MaxMessageSizeInBytes);

                try
                {
                    await PulseConnectionLifecycle.HandleAsync(
                        transportConnection,
                        pulseDispatcher,
                        pulseLogger,
                        httpContext.RequestAborted);
                }
                catch (Exception lifecycleException)
                {
                    pulseLogger.LogError(
                        $"[{connectionId}] Unhandled exception in Pulse protocol middleware",
                        lifecycleException);
                }
            });
        });
    }
}
