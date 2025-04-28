using System;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;
using Rymote.Pulse.Core;
using Rymote.Pulse.Core.Messages;
using Rymote.Pulse.Core.Middleware;
using Rymote.Pulse.Core.Serialization;

namespace Rymote.Pulse.Standalone;

public class StandalonePulseServer : IDisposable
{
    private readonly HttpListener _listener;
    private readonly PulseDispatcher _dispatcher;
    private bool _isRunning;

    public StandalonePulseServer(string prefix, PulseDispatcher dispatcher)
    {
        _listener = new HttpListener();
        _listener.Prefixes.Add(prefix);
        _dispatcher = dispatcher;
    }

    public void Start()
    {
        _isRunning = true;
        _listener.Start();
        _ = AcceptLoopAsync();
        Console.WriteLine("[StandalonePulseServer] Listening on " + _listener.Prefixes.FirstOrDefault());
    }

    private async Task AcceptLoopAsync()
    {
        while (_isRunning)
        {
            HttpListenerContext context;
            try
            {
                context = await _listener.GetContextAsync();
            }
            catch
            {
                if (!_isRunning) 
                    break;
                
                throw;
            }

            if (!context.Request.IsWebSocketRequest)
            {
                context.Response.StatusCode = 400;
                context.Response.Close();
                continue;
            }

            HttpListenerWebSocketContext webSocketContext = await context.AcceptWebSocketAsync(null);
            _ = HandleWebSocketAsync(webSocketContext.WebSocket);
        }
    }

    private async Task HandleWebSocketAsync(WebSocket socket)
    {
        byte[] buffer = new byte[4096];
        List<byte> incomingMessage = new List<byte>();

        try
        {
            while (socket.State == WebSocketState.Open)
            {
                WebSocketReceiveResult result = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);
                if (result.MessageType == WebSocketMessageType.Close)
                    break;

                incomingMessage.AddRange(buffer[..result.Count]);

                if (!result.EndOfMessage) continue;
                PulseRequest? request = null;
                
                try
                {
                    request = JsonSerdes.DeserializeRequest<PulseRequest>(incomingMessage.ToArray());
                }
                catch (Exception ex)
                {
                    PulseResponse errorResponse = new PulseResponse
                    {
                        Status = PulseStatus.BAD_REQUEST,
                        Error = "Invalid request: " + ex.Message
                    };
                    
                    byte[] errorBytes = JsonSerdes.SerializeResponse(errorResponse);
                    await socket.SendAsync(new ArraySegment<byte>(errorBytes), WebSocketMessageType.Binary,
                        true, CancellationToken.None);
                }

                incomingMessage.Clear();

                if (request == null) continue;
                
                bool isStream = request.Kind == PulseKind.STREAM;
                
                Func<PulseContext, Task> chunkSender = async context =>
                    await socket.SendAsync(new ArraySegment<byte>(JsonSerdes.SerializeResponse(context.Response)),
                        WebSocketMessageType.Binary, true, CancellationToken.None);

                PulseResponse response = await _dispatcher.ProcessRequestAsync(request, isStream ? chunkSender : null);
                if (!isStream)
                {
                    byte[] responseBytes = JsonSerdes.SerializeResponse(response);
                    await socket.SendAsync(new ArraySegment<byte>(responseBytes),
                        WebSocketMessageType.Binary, true, CancellationToken.None);
                }
            }

            if (socket.State == WebSocketState.Open)
            {
                await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Server closing connection",
                    CancellationToken.None);
            }
        }
        catch (Exception ex)
        {
            Console.WriteLine("[StandalonePulseServer] WebSocket error: " + ex.Message);
        }
        finally
        {
            socket.Dispose();
        }
    }

    public void Stop()
    {
        _isRunning = false;
        _listener.Stop();
        Console.WriteLine("[StandalonePulseServer] Stopped.");
    }

    public void Dispose()
    {
        Stop();
        _listener.Close();
    }
}