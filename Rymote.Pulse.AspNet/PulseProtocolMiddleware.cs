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
            if (pulseLogger == null)
            {
                throw new ArgumentNullException(nameof(pulseLogger));
            }

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
                    WebSocket webSocketConnection = await httpContext.WebSockets.AcceptWebSocketAsync();
                    PulseConnection connection = await pulseDispatcher.ConnectionManager.AddConnectionAsync(connectionId, webSocketConnection);
                    pulseLogger.LogInfo($"Client connected: {connectionId}");
                    
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
                        await pulseDispatcher.ConnectionManager.RemoveConnectionAsync(connectionId);
                        pulseLogger.LogInfo($"Client disconnected: {connectionId}");
                    }
                });
            });
        }

        private static async Task HandleSocketAsync(
            PulseConnection connection,
            PulseDispatcher pulseDispatcher,
            IPulseLogger pulseLogger)
        {
            const int BufferSizeInBytes = 4096;
            const int MaxMessageSize = 10 * 1024 * 1024; // 10MB limit
            
            var arrayPool = ArrayPool<byte>.Shared;
            byte[] receiveBuffer = arrayPool.Rent(BufferSizeInBytes);
            
            // Use ArraySegment list instead of List<byte>
            var messageSegments = new List<ArraySegment<byte>>();
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
                            
                            // Clean up accumulated segments
                            foreach (var segment in messageSegments)
                            {
                                arrayPool.Return(segment.Array!);
                            }
                            messageSegments.Clear();
                            totalMessageSize = 0;
                            
                            // Close the connection due to oversized message
                            await connection.Socket.CloseAsync(
                                WebSocketCloseStatus.MessageTooBig,
                                "Message exceeds maximum allowed size",
                                CancellationToken.None);
                            break;
                        }
                        
                        // Rent a buffer for this segment
                        byte[] segmentBuffer = arrayPool.Rent(receiveResult.Count);
                        Buffer.BlockCopy(receiveBuffer, 0, segmentBuffer, 0, receiveResult.Count);
                        messageSegments.Add(new ArraySegment<byte>(segmentBuffer, 0, receiveResult.Count));
                    }

                    if (!receiveResult.EndOfMessage)
                    {
                        continue;
                    }

                    // Combine segments efficiently
                    byte[] completeMessageBytes = new byte[totalMessageSize];
                    int offset = 0;
                    
                    foreach (var segment in messageSegments)
                    {
                        Buffer.BlockCopy(segment.Array!, segment.Offset, completeMessageBytes, offset, segment.Count);
                        offset += segment.Count;
                        arrayPool.Return(segment.Array!);
                    }
                    
                    messageSegments.Clear();
                    totalMessageSize = 0;

                    try
                    {
                        await pulseDispatcher.ProcessRawAsync(
                            connection,
                            completeMessageBytes);
                    }
                    catch (Exception processingException)
                    {
                        pulseLogger.LogError(
                            "Error processing incoming Pulse message",
                            processingException);

                        PulseEnvelope<object>? deserializedEnvelope = null;
                        try
                        {
                            deserializedEnvelope = MsgPackSerdes
                                .Deserialize<PulseEnvelope<object>>(completeMessageBytes);
                        }
                        catch (Exception deserializationException)
                        {
                            pulseLogger.LogWarning(
                                $"Failed to deserialize invalid incoming envelope: {deserializationException.Message}");
                        }

                        if (deserializedEnvelope != null)
                        {
                            PulseEnvelope<object?> errorResponseEnvelope = new PulseEnvelope<object?>
                            {
                                Id = deserializedEnvelope.Id,
                                Handle = deserializedEnvelope.Handle,
                                Body = null,
                                AuthToken = deserializedEnvelope.AuthToken,
                                Kind = deserializedEnvelope.Kind,
                                Version = deserializedEnvelope.Version,
                                ClientCorrelationId = deserializedEnvelope.ClientCorrelationId,
                                Status = PulseStatus.BAD_REQUEST,
                                Error = "Invalid message: " + processingException.Message
                            };

                            byte[] errorResponseBytes = MsgPackSerdes.Serialize(errorResponseEnvelope);

                            try
                            {
                                await connection.Socket.SendAsync(
                                    new ArraySegment<byte>(errorResponseBytes),
                                    WebSocketMessageType.Binary,
                                    true,
                                    CancellationToken.None);
                            }
                            catch (Exception sendException)
                            {
                                pulseLogger.LogError(
                                    "Error sending BAD_REQUEST response to client",
                                    sendException);
                            }
                        }
                    }
                }
            }
            finally
            {
                arrayPool.Return(receiveBuffer);
                
                // Return any remaining buffers
                foreach (var segment in messageSegments)
                {
                    arrayPool.Return(segment.Array!);
                }
                
                if (connection.Socket.State == WebSocketState.Open)
                {
                    try
                    {
                        await connection.Socket.CloseAsync(
                            WebSocketCloseStatus.NormalClosure,
                            "Server is closing the connection",
                            CancellationToken.None);
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
            
            // Adaptive sizing based on last message
            int suggestedSize = Math.Max(MinBufferSize, lastMessageSize);
            return Math.Min(suggestedSize, MaxBufferSize);
        }
    }