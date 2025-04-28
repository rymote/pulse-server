using System;
using System.Collections.Generic;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.AspNetCore.Builder;
using Rymote.Pulse.Core;
using Rymote.Pulse.Core.Messages;
using Rymote.Pulse.Core.Middleware;
using Rymote.Pulse.Core.Serialization;

namespace Rymote.Pulse.AspNet;

public static class PulseWebSocketMiddleware
{
    public static IApplicationBuilder UsePulseWebSocket(this IApplicationBuilder app, string path,
        PulseDispatcher dispatcher)
    {
        return app.Map(path, subApp =>
        {
            subApp.Use(async (context, next) =>
            {
                if (!context.WebSockets.IsWebSocketRequest)
                {
                    context.Response.StatusCode = 400;
                    return;
                }

                using WebSocket? socket = await context.WebSockets.AcceptWebSocketAsync();
                await HandleSocketAsync(socket, dispatcher);
            });
        });
    }

    private static async Task HandleSocketAsync(WebSocket socket, PulseDispatcher dispatcher)
    {
        byte[] buffer = new byte[4096];
        List<byte> incomingMessage = new List<byte>();

        while (socket.State == WebSocketState.Open)
        {
            WebSocketReceiveResult result =
                await socket.ReceiveAsync(new ArraySegment<byte>(buffer), CancellationToken.None);

            if (result.MessageType == WebSocketMessageType.Close)
                break;

            incomingMessage.AddRange(buffer[..result.Count]);

            if (!result.EndOfMessage) continue;

            try
            {
                PulseRequest request =
                    JsonSerdes.DeserializeRequest<PulseRequest>(Encoding.UTF8.GetString(incomingMessage.ToArray()));
                bool isStream = request.Kind == PulseKind.STREAM;

                Func<PulseContext, Task> chunkSender = async context =>
                {
                    byte[] chunkBytes = Encoding.UTF8.GetBytes(JsonSerdes.SerializeResponse(context.Response));
                    await socket.SendAsync(new ArraySegment<byte>(chunkBytes), WebSocketMessageType.Binary, true,
                        CancellationToken.None);
                };

                PulseResponse response = await dispatcher.ProcessRequestAsync(request, isStream ? chunkSender : null);

                if (!isStream)
                    await socket.SendAsync(
                        new ArraySegment<byte>(Encoding.UTF8.GetBytes(JsonSerdes.SerializeResponse(response))),
                        WebSocketMessageType.Binary,
                        true, CancellationToken.None);
            }
            catch (System.Exception ex)
            {
                PulseResponse errorResponse = new PulseResponse
                {
                    Status = PulseStatus.BAD_REQUEST,
                    Error = "Invalid message: " + ex.Message
                };

                await socket.SendAsync(
                    new ArraySegment<byte>(Encoding.UTF8.GetBytes(JsonSerdes.SerializeResponse(errorResponse))),
                    WebSocketMessageType.Binary, true,
                    CancellationToken.None);
            }

            incomingMessage.Clear();
        }

        if (socket.State == WebSocketState.Open)
            await socket.CloseAsync(WebSocketCloseStatus.NormalClosure, "Server closed connection",
                CancellationToken.None);
    }
}