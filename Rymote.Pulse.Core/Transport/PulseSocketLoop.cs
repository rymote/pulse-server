using System.Buffers;
using System.Net.WebSockets;
using Rymote.Pulse.Core.Connections;
using Rymote.Pulse.Core.Logging;

namespace Rymote.Pulse.Core.Transport;

public static class PulseSocketLoop
{
    public static async Task RunAsync(
        PulseConnection connection,
        PulseDispatcher dispatcher,
        IPulseLogger logger,
        IPulseSocketLoopOptions options,
        CancellationToken cancellationToken = default)
    {
        int bufferSizeInBytes = options.BufferSizeInBytes;
        int maxMessageSizeInBytes = options.MaxMessageSizeInBytes;

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
                        cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (WebSocketException webSocketException)
                {
                    if (webSocketException.WebSocketErrorCode == WebSocketError.ConnectionClosedPrematurely)
                        break;

                    logger.LogError("Socket exception", webSocketException);
                    break;
                }
                catch (Exception exception)
                {
                    logger.LogError("General exception in Pulse socket loop", exception);
                    break;
                }

                if (receiveResult.MessageType == WebSocketMessageType.Close)
                {
                    logger.LogDebug(
                        $"Client initiated close. Status: {receiveResult.CloseStatus}, " +
                        $"Description: {receiveResult.CloseStatusDescription}");
                    break;
                }

                if (receiveResult.Count > 0)
                {
                    totalMessageSize += receiveResult.Count;

                    if (totalMessageSize > maxMessageSizeInBytes)
                    {
                        logger.LogError($"Message size exceeds maximum allowed size of {maxMessageSizeInBytes} bytes");

                        for (int index = 0; index < segmentCount; index++)
                            arrayPool.Return(messageSegments[index].Array!);

                        segmentCount = 0;
                        totalMessageSize = 0;

                        await connection.Socket.CloseAsync(
                            WebSocketCloseStatus.MessageTooBig,
                            "Message exceeds maximum allowed size",
                            cancellationToken);
                        break;
                    }

                    if (segmentCount >= messageSegments.Length)
                    {
                        logger.LogError("Message has too many segments");
                        await connection.Socket.CloseAsync(
                            WebSocketCloseStatus.MessageTooBig,
                            "Message has too many segments",
                            cancellationToken);
                        break;
                    }

                    byte[] segmentBuffer = arrayPool.Rent(receiveResult.Count);
                    Buffer.BlockCopy(receiveBuffer, 0, segmentBuffer, 0, receiveResult.Count);
                    messageSegments[segmentCount++] = new ArraySegment<byte>(segmentBuffer, 0, receiveResult.Count);
                }

                if (!receiveResult.EndOfMessage)
                    continue;

                try
                {
                    if (segmentCount == 1)
                    {
                        ArraySegment<byte> singleSegment = messageSegments[0];
                        byte[] singleMessageData = new byte[singleSegment.Count];
                        Buffer.BlockCopy(singleSegment.Array!, singleSegment.Offset, singleMessageData, 0,
                            singleSegment.Count);

                        await dispatcher.ProcessRawAsync(connection, singleMessageData);

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

                        await dispatcher.ProcessRawAsync(connection, completeMessageBytes);
                    }

                    segmentCount = 0;
                    totalMessageSize = 0;
                }
                catch (Exception processingException)
                {
                    logger.LogError("Error processing incoming Pulse message", processingException);

                    for (int index = 0; index < segmentCount; index++)
                        arrayPool.Return(messageSegments[index].Array!);

                    segmentCount = 0;
                    totalMessageSize = 0;
                }
            }
        }
        finally
        {
            arrayPool.Return(receiveBuffer);
            arrayPool.Return(messageAssemblyBuffer);

            for (int index = 0; index < segmentCount; index++)
                arrayPool.Return(messageSegments[index].Array!);

            if (connection.IsOpen || connection.Socket.State == WebSocketState.CloseReceived)
            {
                try
                {
                    await dispatcher.ConnectionManager.DisconnectAsync(
                        connection,
                        WebSocketCloseStatus.NormalClosure,
                        "Server is closing the connection",
                        CancellationToken.None);
                    logger.LogDebug("Connection closed cleanly by server");
                }
                catch (Exception closeException)
                {
                    logger.LogError("Error closing connection", closeException);
                }
            }
        }
    }
}
