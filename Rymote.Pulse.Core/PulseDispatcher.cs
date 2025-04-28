using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading.Tasks;
using Rymote.Pulse.Core.Exceptions;
using Rymote.Pulse.Core.Helpers;
using Rymote.Pulse.Core.Logging;
using Rymote.Pulse.Core.Messages;
using Rymote.Pulse.Core.Middleware;
using Rymote.Pulse.Core.Serialization;
using Rymote.Pulse.Core.Streaming;

namespace Rymote.Pulse.Core;

public class PulseDispatcher
{
    private readonly PulseConnectionManager _connManager;
    private readonly IPulseLogger _logger;

    private readonly Dictionary<(string Route, string Version), Func<PulseContext, Task>> _handlers =
        new(new TupleStringComparer());

    private readonly PulseMiddlewarePipeline _pipeline = new();

    public PulseDispatcher(PulseConnectionManager connManager, IPulseLogger logger)
    {
        _connManager = connManager;
        _logger = logger;
    }

    public void Use(PulseMiddlewareDelegate middleware) => _pipeline.Use(middleware);

    public void MapRpc<TRequest, TResponse>(string route, Func<TRequest, PulseContext, Task<TResponse>> func,
        string version = "v1")
        where TRequest : PulseMessage, new()
        where TResponse : PulseMessage, new()
    {
        _handlers[(route.ToLowerInvariant(), version.ToLowerInvariant())] = async context =>
        {
            TRequest requestObj = JsonSerdes.DeserializeRequest<TRequest>(context.Request.Payload);
            TResponse responseObj = await func(requestObj, context);
            context.Response.Data = JsonSerdes.SerializeResponse(responseObj);
        };
    }

    public void MapStream<TRequest, TChunk, THandler>(string route, string version = "v1")
        where THandler : IStreamHandler<TRequest, TChunk>, new()
        where TRequest : PulseMessage, new()
        where TChunk : PulseMessage, new()
    {
        _handlers[(route.ToLowerInvariant(), version.ToLowerInvariant())] = async context =>
        {
            THandler handler = new THandler();
            TRequest requestObj = JsonSerdes.DeserializeRequest<TRequest>(context.Request.Payload);
            PulseStream<TChunk> stream = new PulseStream<TChunk>();

            _ = Task.Run(async () =>
            {
                try
                {
                    await handler.HandleStreamAsync(requestObj, context, stream);
                }
                catch (Exception ex)
                {
                    _logger.LogError("Stream error", ex);
                }
                finally
                {
                    stream.Complete();
                }
            });

            await foreach (TChunk chunk in stream.ReadAllAsync())
            {
                PulseResponse chunkResponse = new PulseResponse
                {
                    Id = context.Response.Id,
                    Response = context.Response.Response,
                    Kind = PulseKind.STREAM,
                    Status = PulseStatus.OK,
                    IsStreamChunk = true,
                    EndOfStream = false,
                    Data = JsonSerdes.SerializeResponse(chunk),
                    ClientCorrelationId = context.Request.ClientCorrelationId
                };

                context.Response = chunkResponse;
                if (context.Items.TryGetValue("SendChunk", out object? senderObj) &&
                    senderObj is Func<PulseResponse, Task> sender)
                {
                    await sender(chunkResponse);
                }
            }

            PulseResponse eos = new PulseResponse
            {
                Id = context.Response.Id,
                Response = context.Response.Response,
                Kind = PulseKind.STREAM,
                Status = PulseStatus.OK,
                IsStreamChunk = true,
                EndOfStream = true,
                ClientCorrelationId = context.Request.ClientCorrelationId
            };

            context.Response = eos;
            if (context.Items.TryGetValue("SendChunk", out object? finalSender) &&
                finalSender is Func<PulseResponse, Task> fs)
            {
                await fs(eos);
            }
        };
    }

    public async Task<PulseResponse> ProcessRequestAsync(PulseRequest request,
        Func<PulseContext, Task>? chunkSender = null)
    {
        request.Id = Ulid.NewUlid().ToString();
        PulseContext context = new PulseContext(_connManager, request, _logger);

        context.Response.ClientCorrelationId = request.ClientCorrelationId;

        if (chunkSender != null)
        {
            context.Items["SendChunk"] = new Func<PulseResponse, Task>(response => chunkSender(context));
        }

        try
        {
            (string, string) key = (request.Request.ToLowerInvariant(), (request.Version ?? "v1").ToLowerInvariant());

            if (!_handlers.TryGetValue(key, out var handler))
            {
                (string Route, string Version) fallback = _handlers.Keys.FirstOrDefault(k => k.Route == key.Item1);
                if (fallback.Equals(default((string, string))))
                    throw new PulseException(PulseStatus.NOT_FOUND,
                        $"Route not found: {request.Request}, v={request.Version}");

                handler = _handlers[fallback];
            }

            await _pipeline.ExecuteAsync(context, handler);
        }
        catch (Exception ex)
        {
            (PulseStatus status, string message) = ErrorMapper.MapException(ex);
            context.Response.Status = status;
            context.Response.Error = message;
            _logger.LogError($"Error processing request: {message}", ex);
        }

        return context.Response;
    }
}