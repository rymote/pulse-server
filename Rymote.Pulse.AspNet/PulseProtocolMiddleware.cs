using System.Net.WebSockets;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Rymote.Pulse.Core;
using Rymote.Pulse.Core.Connections;
using Rymote.Pulse.Core.Logging;
using Rymote.Pulse.Core.Transport;

namespace Rymote.Pulse.AspNet;

public static class PulseProtocolMiddleware
{
    public static IApplicationBuilder UsePulseProtocol(
        this IApplicationBuilder applicationBuilder,
        string websocketPath,
        PulseDispatcher pulseDispatcher,
        IPulseLogger pulseLogger,
        Action<PulseProtocolOptions>? configureOptionsAction = null)
    {
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

                string ipAddress = GetClientIpAddress(httpContext);
                string userAgent = httpContext.Request.Headers["User-Agent"].FirstOrDefault() ?? "Unknown";
                string origin = httpContext.Request.Headers["Origin"].FirstOrDefault() ?? "Unknown";

                string? forwardedFor = httpContext.Request.Headers["X-Forwarded-For"].FirstOrDefault();

                Dictionary<string, string> queryParameters = httpContext.Request.Query
                    .ToDictionary(
                        keyValuePair => keyValuePair.Key,
                        keyValuePair => keyValuePair.Value.ToString());

                WebSocket webSocketConnection = await httpContext.WebSockets.AcceptWebSocketAsync();
                PulseConnection connection =
                    await pulseDispatcher.ConnectionManager.AddConnectionAsync(connectionId, webSocketConnection,
                        queryParameters);

                connection.SetMetadata("http_context", httpContext);
                connection.SetMetadata("ip_address", ipAddress);
                connection.SetMetadata("user_agent", userAgent);
                connection.SetMetadata("origin", origin);
                connection.SetMetadata("connected_at", DateTime.UtcNow);

                pulseLogger.LogInfo(
                    $"[{connectionId}] Client connected: IP: {ipAddress} | Origin: {origin} | UserAgent: {userAgent}");

                if (!string.IsNullOrEmpty(forwardedFor))
                {
                    pulseLogger.LogDebug($"[{connectionId}] Connection forwarded through: {forwardedFor}");
                }

                try
                {
                    await pulseDispatcher.ExecuteOnConnectHandlersAsync(connection);
                }
                catch (Exception onConnectException)
                {
                    pulseLogger.LogError($"[{connectionId}] Error in OnConnect handler", onConnectException);

                    await pulseDispatcher.ConnectionManager.DisconnectAsync(
                        connection,
                        WebSocketCloseStatus.PolicyViolation,
                        onConnectException.Message);

                    return;
                }

                try
                {
                    await PulseSocketLoop.RunAsync(
                        connection,
                        pulseDispatcher,
                        pulseLogger,
                        pulseProtocolOptions);
                }
                catch (Exception loopException)
                {
                    pulseLogger.LogError(
                        "Unhandled exception in Pulse Protocol middleware loop",
                        loopException);
                }
                finally
                {
                    await pulseDispatcher.ExecuteOnDisconnectHandlersAsync(connection);

                    bool connectedAtExists = connection.Metadata.TryGet("connected_at", out DateTime connectedAt);
                    TimeSpan connectedDuration =
                        DateTime.UtcNow - (connectedAtExists ? connectedAt : DateTime.UtcNow);
                    await pulseDispatcher.ConnectionManager.RemoveConnectionAsync(connectionId);

                    string durationText = connectedDuration.TotalHours >= 24
                        ? connectedDuration.ToString(@"d\.hh\:mm\:ss")
                        : connectedDuration.ToString(@"hh\:mm\:ss");

                    pulseLogger.LogInfo(
                        $"[{connectionId}] Client disconnected: IP: {ipAddress} | Duration: {durationText}");
                }
            });
        });
    }

    private static string GetClientIpAddress(HttpContext httpContext)
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
