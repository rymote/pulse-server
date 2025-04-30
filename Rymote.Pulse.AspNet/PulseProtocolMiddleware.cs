using System;
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
            byte[] receiveBuffer = new byte[BufferSizeInBytes];
            ArraySegment<byte> receiveBufferSegment = new ArraySegment<byte>(receiveBuffer);
            List<byte> messageAccumulator = new List<byte>();

            try
            {
                while (connection.Socket.State == WebSocketState.Open)
                {
                    WebSocketReceiveResult receiveResult;

                    try
                    {
                        receiveResult = await connection.Socket.ReceiveAsync(
                            receiveBufferSegment,
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

                    messageAccumulator.AddRange(
                        receiveBuffer.AsSpan(0, receiveResult.Count).ToArray());

                    if (!receiveResult.EndOfMessage)
                    {
                        continue;
                    }

                    byte[] completeMessageBytes = messageAccumulator.ToArray();
                    messageAccumulator.Clear();

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

                        PulseEnvelope<object> deserializedEnvelope = null;
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
                            PulseEnvelope<object> errorResponseEnvelope = new PulseEnvelope<object>
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
    }