using System;
using System.Buffers;
using System.Collections.Generic;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using Rymote.Pulse.Core;
using Rymote.Pulse.Core.Connections;
using Rymote.Pulse.Core.Logging;
using Rymote.Pulse.Core.Messages;
using Rymote.Pulse.Core.Middleware;
using Rymote.Pulse.Core.Serialization;

namespace Rymote.Pulse.AspNet;

public static class PulseProtocolMiddleware
{
    public static IApplicationBuilder UsePulseProtocol(
        this IApplicationBuilder applicationBuilder,
        string websocketPath,
        PulseDispatcher pulseDispatcher,
        IPulseLogger pulseLogger)
    {
        ArgumentNullException.ThrowIfNull(pulseLogger);

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
                    string? protocol = httpContext.Request.Protocol;

                    string? forwardedFor = httpContext.Request.Headers["X-Forwarded-For"].FirstOrDefault();
                    string? realIp = httpContext.Request.Headers["X-Real-IP"].FirstOrDefault();

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
                        pulseLogger.LogInfo($"[{connectionId}] Connection forwarded through: {forwardedFor}");
                    }

                    try
                    {
                        await pulseDispatcher.ExecuteOnConnectHandlersAsync(connection);
                    }
                    catch (Exception exception)
                    {
                        pulseLogger.LogError($"[{connectionId}] Error in OnConnect handler", exception);

                        await pulseDispatcher.ConnectionManager.DisconnectAsync(
                            connection,
                            WebSocketCloseStatus.PolicyViolation,
                            exception.Message);

                        return;
                    }

                    try
                    {
                        await HandleSocketAsync(
                            connection,
                            pulseDispatcher,
                            pulseLogger);
                    }
                    catch (Exception middlewareException)
                    {
                        pulseLogger.LogError(
                            "Unhandled exception in Pulse Protocol middleware loop",
                            middlewareException);
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
            }
        );
    }

    private static string GetClientIpAddress(HttpContext context)
    {
        string? forwardedFor = context.Request.Headers["X-Forwarded-For"].FirstOrDefault();
        if (!string.IsNullOrEmpty(forwardedFor))
            return forwardedFor.Split(',')[0].Trim();


        string? realIp = context.Request.Headers["X-Real-IP"].FirstOrDefault();
        if (!string.IsNullOrEmpty(realIp))
            return realIp;

        return context.Connection.RemoteIpAddress?.ToString() ?? "Unknown";
    }

    private static async Task HandleSocketAsync(
        PulseConnection connection,
        PulseDispatcher pulseDispatcher,
        IPulseLogger pulseLogger)
    {
        const int BufferSizeInBytes = 4096;
        const int MaxMessageSize = 10 * 1024 * 1024; // 10MB limit

        ArrayPool<byte> arrayPool = ArrayPool<byte>.Shared;
        byte[] receiveBuffer = arrayPool.Rent(BufferSizeInBytes);

        byte[] messageAssemblyBuffer = arrayPool.Rent(MaxMessageSize);

        ArraySegment<byte>[] messageSegments = new ArraySegment<byte>[256];
        int segmentCount = 0;
        int totalMessageSize = 0;

        try
        {
            while (connection.Socket.State == WebSocketState.Open)
            {
                WebSocketReceiveResult receiveResult;

                try
                {
                    receiveResult = await connection.Socket.ReceiveAsync(
                        new ArraySegment<byte>(receiveBuffer),
                        CancellationToken.None);
                }
                catch (WebSocketException webSocketException)
                {
                    if (webSocketException.WebSocketErrorCode == WebSocketError.ConnectionClosedPrematurely)
                        break;

                    pulseLogger.LogError("Socket exception", webSocketException);
                    break;
                }
                catch (Exception exception)
                {
                    pulseLogger.LogError(
                        "General exception in Pulse Protocol middleware loop",
                        exception);
                    break;
                }

                if (receiveResult.MessageType == WebSocketMessageType.Close)
                {
                    pulseLogger.LogInfo(
                        $"Client initiated close. Status: {receiveResult.CloseStatus}, " +
                        $"Description: {receiveResult.CloseStatusDescription}");
                    break;
                }

                if (receiveResult.Count > 0)
                {
                    totalMessageSize += receiveResult.Count;

                    if (totalMessageSize > MaxMessageSize)
                    {
                        pulseLogger.LogError($"Message size exceeds maximum allowed size of {MaxMessageSize} bytes");

                        for (int i = 0; i < segmentCount; i++)
                            arrayPool.Return(messageSegments[i].Array!);

                        segmentCount = 0;
                        totalMessageSize = 0;

                        await connection.Socket.CloseAsync(
                            WebSocketCloseStatus.MessageTooBig,
                            "Message exceeds maximum allowed size",
                            CancellationToken.None);
                        break;
                    }

                    if (segmentCount >= messageSegments.Length)
                    {
                        pulseLogger.LogError("Message has too many segments");
                        await connection.Socket.CloseAsync(
                            WebSocketCloseStatus.MessageTooBig,
                            "Message has too many segments",
                            CancellationToken.None);
                        break;
                    }

                    byte[] segmentBuffer = arrayPool.Rent(receiveResult.Count);
                    Buffer.BlockCopy(receiveBuffer, 0, segmentBuffer, 0, receiveResult.Count);
                    messageSegments[segmentCount++] = new ArraySegment<byte>(segmentBuffer, 0, receiveResult.Count);
                }

                if (!receiveResult.EndOfMessage)
                {
                    continue;
                }

                try
                {
                    if (segmentCount == 1)
                    {
                        ArraySegment<byte> singleSegment = messageSegments[0];
                        byte[] singleMessageData = new byte[singleSegment.Count];
                        Buffer.BlockCopy(singleSegment.Array!, singleSegment.Offset, singleMessageData, 0,
                            singleSegment.Count);

                        await pulseDispatcher.ProcessRawAsync(connection, singleMessageData);

                        arrayPool.Return(singleSegment.Array!);
                    }
                    else if (segmentCount > 1)
                    {
                        int offset = 0;
                        for (int i = 0; i < segmentCount; i++)
                        {
                            ArraySegment<byte> segment = messageSegments[i];
                            Buffer.BlockCopy(segment.Array!, segment.Offset, messageAssemblyBuffer, offset,
                                segment.Count);
                            offset += segment.Count;
                            arrayPool.Return(segment.Array!);
                        }

                        byte[] completeMessageBytes = new byte[totalMessageSize];
                        Buffer.BlockCopy(messageAssemblyBuffer, 0, completeMessageBytes, 0, totalMessageSize);

                        await pulseDispatcher.ProcessRawAsync(connection, completeMessageBytes);
                    }

                    segmentCount = 0;
                    totalMessageSize = 0;
                }
                catch (Exception processingException)
                {
                    pulseLogger.LogError(
                        "Error processing incoming Pulse message",
                        processingException);

                    for (int i = 0; i < segmentCount; i++)
                        arrayPool.Return(messageSegments[i].Array!);

                    segmentCount = 0;
                    totalMessageSize = 0;

                    PulseEnvelope<object>? deserializedEnvelope = null;
                    try
                    {
                    }
                    catch (Exception deserializationException)
                    {
                        pulseLogger.LogWarning(
                            $"Failed to deserialize invalid incoming envelope: {deserializationException.Message}");
                    }
                }
            }
        }
        finally
        {
            arrayPool.Return(receiveBuffer);
            arrayPool.Return(messageAssemblyBuffer);

            for (int i = 0; i < segmentCount; i++)
                arrayPool.Return(messageSegments[i].Array!);

            if (connection.IsOpen)
            {
                try
                {
                    await pulseDispatcher.ConnectionManager.DisconnectAsync(connection,
                        WebSocketCloseStatus.NormalClosure, "Server is closing the connection", CancellationToken.None);
                    pulseLogger.LogInfo("Connection closed cleanly by server");
                }
                catch (Exception closeException)
                {
                    pulseLogger.LogError(
                        "Error closing connection",
                        closeException);
                }
            }
        }
    }

    private static int CalculateBufferSize(int lastMessageSize)
    {
        const int MinBufferSize = 1024;
        const int MaxBufferSize = 64 * 1024;

        int suggestedSize = Math.Max(MinBufferSize, lastMessageSize);
        return Math.Min(suggestedSize, MaxBufferSize);
    }
}