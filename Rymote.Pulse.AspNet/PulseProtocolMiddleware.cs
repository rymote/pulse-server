using System.Buffers;
using System.Net.WebSockets;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Rymote.Pulse.Core;
using Rymote.Pulse.Core.Connections;
using Rymote.Pulse.Core.Logging;
using Rymote.Pulse.Core.Messages;

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
                            pulseLogger,
                            pulseProtocolOptions);
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
        IPulseLogger pulseLogger,
        PulseProtocolOptions pulseProtocolOptions)
    {
        int bufferSizeInBytes = pulseProtocolOptions.BufferSizeInBytes;
        int maxMessageSizeInBytes = pulseProtocolOptions.MaxMessageSizeInBytes;
        
        ArrayPool<byte> arrayPool = ArrayPool<byte>.Shared;
        byte[] receiveBuffer = arrayPool.Rent(bufferSizeInBytes);

        byte[] messageAssemblyBuffer = arrayPool.Rent(maxMessageSizeInBytes);

        int maximumSegments = (maxMessageSizeInBytes + bufferSizeInBytes - 1) / bufferSizeInBytes;
        ArraySegment<byte>[] messageSegments = new ArraySegment<byte>[maximumSegments];
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

                    if (totalMessageSize > maxMessageSizeInBytes)
                    {
                        pulseLogger.LogError($"Message size exceeds maximum allowed size of {maxMessageSizeInBytes} bytes");

                        for (int index = 0; index < segmentCount; index++)
                            arrayPool.Return(messageSegments[index].Array!);

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
                        for (int index = 0; index < segmentCount; index++)
                        {
                            ArraySegment<byte> segment = messageSegments[index];
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

                    for (int index = 0; index < segmentCount; index++)
                        arrayPool.Return(messageSegments[index].Array!);

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

            for (int index = 0; index < segmentCount; index++)
                arrayPool.Return(messageSegments[index].Array!);

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