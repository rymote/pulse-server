using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Net.WebSockets;
using System.Threading.Channels;
using System.Threading.Tasks;
using Rymote.Pulse.Core.Connections;
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
    public readonly PulseConnectionManager ConnectionManager;
    private readonly IPulseLogger _logger;
    private readonly Dictionary<(string Handle, string Version), Func<PulseContext, Task>> _handlers;
    private readonly PulseMiddlewarePipeline _pipeline;
    private readonly ConcurrentDictionary<string, Channel<byte[]>> _inboundStreams;

    public PulseDispatcher(PulseConnectionManager connectionManager, IPulseLogger logger)
    {
        ConnectionManager = connectionManager;
        
        _logger = logger;
        _handlers = new Dictionary<(string, string), Func<PulseContext, Task>>(new TupleStringComparer());
        _pipeline = new PulseMiddlewarePipeline();
        _inboundStreams = new ConcurrentDictionary<string, Channel<byte[]>>();
    }

    public void Use(PulseMiddlewareDelegate middleware)
    {
        _pipeline.Use(middleware);
    }

    public void MapRpc<TRequest, TResponse>(
        string handle,
        Func<TRequest, PulseContext, Task<TResponse>> handlerFunc,
        string version = "v1")
        where TRequest : class, new()
        where TResponse : class, new()
    {
        _handlers[(handle.ToLowerInvariant(), version.ToLowerInvariant())] = async context =>
        {
            PulseEnvelope<TRequest> requestEnvelope = context.GetTypedRequestEnvelope<TRequest>();
            TResponse responseBody = await handlerFunc(requestEnvelope.Body, context);
            
            PulseEnvelope<TResponse> responseEnvelope = new PulseEnvelope<TResponse>
            {
                Handle = requestEnvelope.Handle,
                Body = responseBody,
                AuthToken = requestEnvelope.AuthToken,
                Kind = PulseKind.RPC,
                Version = requestEnvelope.Version,
                ClientCorrelationId = requestEnvelope.ClientCorrelationId,
                Status = PulseStatus.OK
            };
            
            context.TypedResponseEnvelope = responseEnvelope;
        };
    }

    public void MapRpcStream<TRequest, TResponse>(
        string handle,
        Func<TRequest, PulseContext, IAsyncEnumerable<TResponse>> handlerFunc,
        string version = "v1")
        where TRequest : class, new()
        where TResponse : class, new()
    {
        _handlers[(handle.ToLowerInvariant(), version.ToLowerInvariant())] = async context =>
        {
            PulseEnvelope<TRequest> requestEnvelope = context.GetTypedRequestEnvelope<TRequest>();
            IAsyncEnumerable<TResponse> responses = handlerFunc(requestEnvelope.Body, context);
            
            await foreach (TResponse chunk in responses)
            {
                PulseEnvelope<TResponse> chunkEnvelope = new PulseEnvelope<TResponse>
                {
                    Handle = requestEnvelope.Handle,
                    Body = chunk,
                    AuthToken = requestEnvelope.AuthToken,
                    Kind = PulseKind.STREAM,
                    Version = requestEnvelope.Version,
                    ClientCorrelationId = requestEnvelope.ClientCorrelationId,
                    IsStreamChunk = true,
                    EndOfStream = false
                };
                
                if (context.SendChunkAsync != null)
                    await context.SendChunkAsync(MsgPackSerdes.Serialize(chunkEnvelope));
            }

            PulseEnvelope<TResponse> endOfStreamEnvelope = new PulseEnvelope<TResponse>
            {
                Handle = requestEnvelope.Handle,
                Body = default!,
                AuthToken = requestEnvelope.AuthToken,
                Kind = PulseKind.STREAM,
                Version = requestEnvelope.Version,
                ClientCorrelationId = requestEnvelope.ClientCorrelationId,
                IsStreamChunk = true,
                EndOfStream = true
            };
            
            if (context.SendChunkAsync != null)
                await context.SendChunkAsync(MsgPackSerdes.Serialize(endOfStreamEnvelope));
        };
    }

    public void MapClientStream<TChunk>(
        string handle,
        Func<IAsyncEnumerable<TChunk>, PulseContext, Task> handlerFunc,
        string version = "v1")
        where TChunk : class, new()
    {
        _handlers[(handle.ToLowerInvariant(), version.ToLowerInvariant())] = async context =>
        {
            string? clientCorrelationId = context.UntypedRequest.ClientCorrelationId;
            if (clientCorrelationId != null && _inboundStreams.TryRemove(clientCorrelationId, out Channel<byte[]> channel))
            {
                async IAsyncEnumerable<TChunk> GetChunks(ChannelReader<byte[]> reader)
                {
                    await foreach (byte[] bytes in reader.ReadAllAsync())
                    {
                        PulseEnvelope<TChunk> envelope = MsgPackSerdes.Deserialize<PulseEnvelope<TChunk>>(bytes);
                        yield return envelope.Body;
                    }
                }
                
                IAsyncEnumerable<TChunk> chunks = GetChunks(channel.Reader);
                await handlerFunc(chunks, context);
            }

            context.TypedResponseEnvelope = null;
        };
    }

    public void MapEvent<TEvent>(
        string handle,
        Func<TEvent, PulseContext, Task> handlerFunc,
        string version = "v1")
        where TEvent : class, new()
    {
        _handlers[(handle.ToLowerInvariant(), version.ToLowerInvariant())] = async context =>
        {
            PulseEnvelope<TEvent> eventEnvelope = context.GetTypedRequestEnvelope<TEvent>();
            await handlerFunc(eventEnvelope.Body, context);
            context.TypedResponseEnvelope = null;
        };
    }
    
    public void SendEvent<TEvent>(
        string handle,
        Func<TEvent, PulseContext, Task> handlerFunc,
        string version = "v1")
        where TEvent : class, new()
    {
        _handlers[(handle.ToLowerInvariant(), version.ToLowerInvariant())] = async context =>
        {
            PulseEnvelope<TEvent> eventEnvelope = context.GetTypedRequestEnvelope<TEvent>();
            await handlerFunc(eventEnvelope.Body, context);
            context.TypedResponseEnvelope = null;
        };
    }

    public async Task ProcessRawAsync(PulseConnection connection, byte[] rawData)
    {
        PulseEnvelope<object> envelope = MsgPackSerdes.Deserialize<PulseEnvelope<object>>(rawData);
        string handleKey = envelope.Handle.ToLowerInvariant();
        string versionKey = envelope.Version.ToLowerInvariant();
        (string handleKey, string versionKey) key = (handleKey, versionKey);

        if (envelope.Kind == PulseKind.STREAM && envelope.IsStreamChunk.HasValue && envelope.IsStreamChunk.Value && envelope.ClientCorrelationId != null)
        {
            Channel<byte[]> channel = _inboundStreams.GetOrAdd(
                envelope.ClientCorrelationId,
                _ => Channel.CreateUnbounded<byte[]>());
            
            await channel.Writer.WriteAsync(rawData);
            
            if (envelope.EndOfStream.HasValue && envelope.EndOfStream.Value)
                channel.Writer.Complete();
            
            return;
        }

        if (!_handlers.TryGetValue(key, out Func<PulseContext, Task>? handler))
            throw new PulseException(PulseStatus.NOT_FOUND, $"Handle not found: {envelope.Handle}");

        PulseContext context = new PulseContext(ConnectionManager, connection, rawData, _logger)
        {
            SendChunkAsync = async bytes =>
            {
                await connection.Socket.SendAsync(
                    new ArraySegment<byte>(bytes),
                    WebSocketMessageType.Binary,
                    true,
                    CancellationToken.None);
            }
        };

        await _pipeline.ExecuteAsync(context, handler);

        if (context.TypedResponseEnvelope != null)
        {
            byte[] responseBytes = MsgPackSerdes.Serialize(context.TypedResponseEnvelope);
            await connection.Socket.SendAsync(
                new ArraySegment<byte>(responseBytes),
                WebSocketMessageType.Binary,
                true,
                CancellationToken.None);
        }
    }
}