using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Threading.Channels;
using System.Text.RegularExpressions;
using Rymote.Pulse.Core.Connections;
using Rymote.Pulse.Core.Exceptions;
using Rymote.Pulse.Core.Logging;
using Rymote.Pulse.Core.Messages;
using Rymote.Pulse.Core.Middleware;
using Rymote.Pulse.Core.Serialization;

namespace Rymote.Pulse.Core;

public class PulseDispatcher : IDisposable
{
    public readonly PulseConnectionManager ConnectionManager;
    
    private readonly List<Func<PulseConnection, Task>> _onConnectHandlers = [];
    private readonly List<Func<PulseConnection, Task>> _onDisconnectHandlers = [];
    
    [Obsolete("Use AddOnConnectHandler instead to support multiple handlers")]
    public Func<PulseConnection, Task>? OnConnect 
    { 
        get => _onConnectHandlers.FirstOrDefault();
        set 
        { 
            _onConnectHandlers.Clear();
            if (value != null) _onConnectHandlers.Add(value);
        }
    }
    
    private readonly IPulseLogger _logger;
    private readonly List<(HandlePattern HandlePattern, string Version, Func<PulseContext, Task> Handler)> _handlers;
    private readonly PulseMiddlewarePipeline _pipeline;
    private readonly ConcurrentDictionary<string, (Channel<byte[]> Channel, DateTime LastActivity)> _inboundStreams;
    private readonly Timer _cleanupTimer;

    public PulseDispatcher(PulseConnectionManager connectionManager, IPulseLogger logger)
    {
        ConnectionManager = connectionManager;
        
        _logger = logger;
        _handlers = new List<(HandlePattern HandlePattern, string Version, Func<PulseContext, Task> Handler)>();
        
        _pipeline = new PulseMiddlewarePipeline();
        _inboundStreams = new ConcurrentDictionary<string, (Channel<byte[]>, DateTime)>();
        _cleanupTimer = new Timer(CleanupAbandonedStreams, null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
    }

    private void CleanupAbandonedStreams(object? state)
    {
        DateTime cutoffTime = DateTime.UtcNow.AddMinutes(-5);
        List<string> keysToRemove = _inboundStreams
            .Where(keyValuePair => keyValuePair.Value.LastActivity < cutoffTime)
            .Select(keyValuePair => keyValuePair.Key)
            .ToList();

        foreach (string key in keysToRemove)
        {
            if (!_inboundStreams.TryRemove(key, out (Channel<byte[]> Channel, DateTime LastActivity) streamInfo)) continue;
            
            streamInfo.Channel.Writer.TryComplete();
            _logger?.LogInfo($"Cleaned up abandoned stream: {key}");
        }
    }

    public void AddOnConnectHandler(Func<PulseConnection, Task> handler)
    {
        _onConnectHandlers.Add(handler);
    }
    
    public async Task ExecuteOnConnectHandlersAsync(PulseConnection connection)
    {
        foreach (Func<PulseConnection, Task> handler in _onConnectHandlers)
        {
            try
            {
                await handler(connection);
            }
            catch (Exception exception)
            {
                _logger.LogError($"Error in OnConnect handler", exception);
                throw;
            }
        }
    }
    
    public void AddOnDisconnectHandler(Func<PulseConnection, Task> handler)
    {
        _onDisconnectHandlers.Add(handler);
    }
    
    public async Task ExecuteOnDisconnectHandlersAsync(PulseConnection connection)
    {
        foreach (var handler in _onDisconnectHandlers)
        {
            try
            {
                await handler(connection);
            }
            catch (Exception exception)
            {
                _logger?.LogError($"Error in OnDisconnect handler", exception);
            }
        }
    }
    
    public void Use(PulseMiddlewareDelegate middleware)
    {
        _pipeline.Use(middleware);
    }

    public void MapRpc<TResponse>(
        string handle,
        Func<PulseContext, Task<TResponse>> handlerFunc,
        string version = "v1")
        where TResponse : class, new()
    {
        _handlers.Add((new HandlePattern(handle), version.ToLowerInvariant(), async context =>
        {
            TResponse responseBody = await handlerFunc(context);
            
            PulseEnvelope<TResponse> responseEnvelope = new PulseEnvelope<TResponse>
            {
                Handle = context.UntypedRequest.Handle,
                Body = responseBody,
                AuthToken = context.UntypedRequest.AuthToken,
                Kind = PulseKind.RPC,
                Version = context.UntypedRequest.Version,
                ClientCorrelationId = context.UntypedRequest.ClientCorrelationId,
                Status = PulseStatus.OK
            };
            
            context.TypedResponseEnvelope = responseEnvelope;
        }));
    }
    
    public void MapRpc<TRequest, TResponse>(
        string handle,
        Func<TRequest, PulseContext, Task<TResponse>> handlerFunc,
        string version = "v1")
        where TRequest : class, new()
        where TResponse : class, new()
    {
        _handlers.Add((new HandlePattern(handle), version.ToLowerInvariant(), async context =>
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
        }));
    }

    public void MapRpcStream<TRequest, TResponse>(
        string handle,
        Func<TRequest, PulseContext, IAsyncEnumerable<TResponse>> handlerFunc,
        string version = "v1")
        where TRequest : class, new()
        where TResponse : class, new()
    {
        _handlers.Add((new HandlePattern(handle), version.ToLowerInvariant(), async context =>
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
        }));
    }

    public void MapClientStream<TChunk>(
        string handle,
        Func<IAsyncEnumerable<TChunk>, PulseContext, Task> handlerFunc,
        string version = "v1")
        where TChunk : class, new()
    {
        _handlers.Add((new HandlePattern(handle), version.ToLowerInvariant(), async context =>
        {
            string? clientCorrelationId = context.UntypedRequest.ClientCorrelationId;
            if (clientCorrelationId != null && _inboundStreams.TryRemove(clientCorrelationId, out (Channel<byte[]> Channel, DateTime LastActivity) streamInfo))
            {
                async IAsyncEnumerable<TChunk> GetChunks(ChannelReader<byte[]> reader)
                {
                    await foreach (byte[] bytes in reader.ReadAllAsync())
                    {
                        PulseEnvelope<TChunk> envelope = MsgPackSerdes.Deserialize<PulseEnvelope<TChunk>>(bytes);
                        yield return envelope.Body;
                    }
                }
                
                IAsyncEnumerable<TChunk> chunks = GetChunks(streamInfo.Channel.Reader);
                await handlerFunc(chunks, context);
            }

            context.TypedResponseEnvelope = null;
        }));
    }

    public void MapEvent(
        string handle,
        Func<PulseContext, Task> handlerFunc,
        string version = "v1")
    {
        _handlers.Add((new HandlePattern(handle), version.ToLowerInvariant(), async context =>
        {
            await handlerFunc(context);
            context.TypedResponseEnvelope = null;
        }));
    }

    public void MapEvent<TEvent>(
        string handle,
        Func<TEvent, PulseContext, Task> handlerFunc,
        string version = "v1")
        where TEvent : class, new()
    {
        _handlers.Add((new HandlePattern(handle), version.ToLowerInvariant(), async context =>
        {
            PulseEnvelope<TEvent> eventEnvelope = context.GetTypedRequestEnvelope<TEvent>();
            await handlerFunc(eventEnvelope.Body, context);
            context.TypedResponseEnvelope = null;
        }));
    }
    
    public void SendEvent<TEvent>(
        string handle,
        Func<TEvent, PulseContext, Task> handlerFunc,
        string version = "v1")
        where TEvent : class, new()
    {
        _handlers.Add((new HandlePattern(handle), version.ToLowerInvariant(), async context =>
        {
            PulseEnvelope<TEvent> eventEnvelope = context.GetTypedRequestEnvelope<TEvent>();
            await handlerFunc(eventEnvelope.Body, context);
            context.TypedResponseEnvelope = null;
        }));
    }

    public async Task ProcessRawAsync(PulseConnection connection, byte[] rawData)
    {
        PulseEnvelope<object> envelope = MsgPackSerdes.Deserialize<PulseEnvelope<object>>(rawData);
        string handleKey = envelope.Handle.ToLowerInvariant();
        string versionKey = envelope.Version.ToLowerInvariant();

        if (envelope.Kind == PulseKind.STREAM && envelope.IsStreamChunk.HasValue && envelope.IsStreamChunk.Value && envelope.ClientCorrelationId != null)
        {
            (Channel<byte[]> Channel, DateTime LastActivity) streamInfo = _inboundStreams.GetOrAdd(
                envelope.ClientCorrelationId,
                _ => (Channel.CreateUnbounded<byte[]>(), DateTime.UtcNow));
            
            _inboundStreams[envelope.ClientCorrelationId] = (streamInfo.Channel, DateTime.UtcNow);
            
            await streamInfo.Channel.Writer.WriteAsync(rawData);

            if (!envelope.EndOfStream.HasValue || !envelope.EndOfStream.Value) return;
            streamInfo.Channel.Writer.Complete();
            
            _inboundStreams.TryRemove(envelope.ClientCorrelationId, out _);

            return;
        }

        Func<PulseContext, Task>? handler = null;
        Dictionary<string, string> parameters = new Dictionary<string, string>();

        foreach ((HandlePattern handlePattern, string version, Func<PulseContext, Task> handlerFunc) in _handlers)
        {
            if (version != versionKey) continue;
            
            Match match = handlePattern.Regex.Match(handleKey);
            if (!match.Success) continue;
            
            handler = handlerFunc;
            foreach (string groupName in handlePattern.Regex.GetGroupNames())
            {
                if (match.Groups[groupName].Success)
                {
                    parameters[groupName] = match.Groups[groupName].Value;
                }
            }
            
            break;
        }

        if (handler == null)
            throw new PulseException(PulseStatus.NOT_FOUND, $"Handle not found: {envelope.Handle}");

        PulseContext context = new PulseContext(ConnectionManager, connection, rawData, _logger, parameters)
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

    public void Dispose()
    {
        _cleanupTimer?.Dispose();
        
        foreach (KeyValuePair<string, (Channel<byte[]> Channel, DateTime LastActivity)> keyValuePair in _inboundStreams)
        {
            keyValuePair.Value.Channel.Writer.TryComplete();
        }
        _inboundStreams.Clear();
    }
}