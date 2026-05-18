using System.Buffers;
using System.Net.WebSockets;
using Microsoft.AspNetCore.Http;
using Rymote.Pulse.Core.Transport;

namespace Rymote.Pulse.Transports.WebSockets.AspNetCore;

internal sealed class WebSocketAspNetCoreConnection : IPulseTransportConnection
{
    public string ConnectionId { get; }
    public string TransportName => "websocket-aspnetcore";
    public bool IsOpen => _webSocket.State == WebSocketState.Open;
    public IReadOnlyDictionary<string, string> QueryParameters { get; }
    public IReadOnlyDictionary<string, object> InitialMetadata { get; }

    private readonly WebSocket _webSocket;
    private readonly int _bufferSizeInBytes;
    private readonly int _maxMessageSizeInBytes;
    private bool _disposed;

    public WebSocketAspNetCoreConnection(
        string connectionId,
        WebSocket webSocket,
        HttpContext httpContext,
        int bufferSizeInBytes,
        int maxMessageSizeInBytes)
    {
        ConnectionId = connectionId;
        _webSocket = webSocket;
        _bufferSizeInBytes = bufferSizeInBytes;
        _maxMessageSizeInBytes = maxMessageSizeInBytes;

        QueryParameters = httpContext.Request.Query.ToDictionary(
            keyValuePair => keyValuePair.Key,
            keyValuePair => keyValuePair.Value.ToString());

        InitialMetadata = BuildInitialMetadata(webSocket, httpContext);
    }

    private static IReadOnlyDictionary<string, object> BuildInitialMetadata(WebSocket webSocket, HttpContext httpContext)
    {
        return new Dictionary<string, object>
        {
            ["websocket"] = webSocket,
            ["http_context"] = httpContext,
            ["ip_address"] = ResolveClientIpAddress(httpContext),
            ["user_agent"] = httpContext.Request.Headers["User-Agent"].FirstOrDefault() ?? "Unknown",
            ["origin"] = httpContext.Request.Headers["Origin"].FirstOrDefault() ?? "Unknown"
        };
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

    public async ValueTask<ReadOnlyMemory<byte>?> ReceiveMessageAsync(CancellationToken cancellationToken)
    {
        ArrayPool<byte> arrayPool = ArrayPool<byte>.Shared;
        byte[] receiveBuffer = arrayPool.Rent(_bufferSizeInBytes);
        byte[]? messageAssemblyBuffer = null;
        int totalMessageSize = 0;

        try
        {
            while (true)
            {
                WebSocketReceiveResult receiveResult;
                try
                {
                    receiveResult = await _webSocket.ReceiveAsync(
                        new ArraySegment<byte>(receiveBuffer),
                        cancellationToken).ConfigureAwait(false);
                }
                catch (WebSocketException webSocketException)
                {
                    if (webSocketException.WebSocketErrorCode == WebSocketError.ConnectionClosedPrematurely)
                        return null;
                    throw;
                }

                if (receiveResult.MessageType == WebSocketMessageType.Close)
                    return null;

                if (receiveResult.Count > 0)
                {
                    totalMessageSize += receiveResult.Count;

                    if (totalMessageSize > _maxMessageSizeInBytes)
                    {
                        await _webSocket.CloseAsync(
                            WebSocketCloseStatus.MessageTooBig,
                            "Message exceeds maximum allowed size",
                            cancellationToken).ConfigureAwait(false);
                        return null;
                    }

                    if (messageAssemblyBuffer == null)
                        messageAssemblyBuffer = arrayPool.Rent(Math.Max(_bufferSizeInBytes, totalMessageSize));
                    else if (totalMessageSize > messageAssemblyBuffer.Length)
                    {
                        byte[] grown = arrayPool.Rent(totalMessageSize);
                        Buffer.BlockCopy(messageAssemblyBuffer, 0, grown, 0, totalMessageSize - receiveResult.Count);
                        arrayPool.Return(messageAssemblyBuffer);
                        messageAssemblyBuffer = grown;
                    }

                    Buffer.BlockCopy(
                        receiveBuffer, 0,
                        messageAssemblyBuffer, totalMessageSize - receiveResult.Count,
                        receiveResult.Count);
                }

                if (receiveResult.EndOfMessage)
                {
                    byte[] completeMessage = new byte[totalMessageSize];
                    if (messageAssemblyBuffer != null && totalMessageSize > 0)
                        Buffer.BlockCopy(messageAssemblyBuffer, 0, completeMessage, 0, totalMessageSize);
                    return completeMessage;
                }
            }
        }
        finally
        {
            arrayPool.Return(receiveBuffer);
            if (messageAssemblyBuffer != null)
                arrayPool.Return(messageAssemblyBuffer);
        }
    }

    public async ValueTask SendMessageAsync(ReadOnlyMemory<byte> payload, CancellationToken cancellationToken)
    {
        await _webSocket.SendAsync(
            payload,
            WebSocketMessageType.Binary,
            endOfMessage: true,
            cancellationToken).ConfigureAwait(false);
    }

    public async ValueTask CloseAsync(int closeCode, string? reason, CancellationToken cancellationToken)
    {
        if (_webSocket.State != WebSocketState.Open && _webSocket.State != WebSocketState.CloseReceived)
            return;

        try
        {
            await _webSocket.CloseAsync(
                (WebSocketCloseStatus)closeCode,
                reason ?? "Connection closed",
                cancellationToken).ConfigureAwait(false);
        }
        catch (WebSocketException)
        {
            // socket already gone — ignore
        }
        catch (ObjectDisposedException)
        {
            // ignore
        }
    }

    public ValueTask DisposeAsync()
    {
        if (_disposed) return ValueTask.CompletedTask;
        _disposed = true;
        _webSocket.Dispose();
        GC.SuppressFinalize(this);
        return ValueTask.CompletedTask;
    }
}
