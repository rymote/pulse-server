using System.Buffers;
using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Threading.Channels;
using System.Text.RegularExpressions;
using MessagePack;
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
    private readonly PulseMiddlewarePipeline _pipeline;

    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, Func<PulseContext, Task>>> _fastHandlers;
    private readonly List<(Regex CompiledRegex, string Version, Func<PulseContext, Task> Handler, string[] GroupNames)>
        _regexHandlers;

    private readonly ReaderWriterLockSlim _regexHandlersLock;

    public PulseDispatcher(PulseConnectionManager connectionManager, IPulseLogger logger)
    {
        ConnectionManager = connectionManager;

        _logger = logger;
        
        _fastHandlers =
            new ConcurrentDictionary<string, ConcurrentDictionary<string, Func<PulseContext, Task>>>(StringComparer
                .OrdinalIgnoreCase);
        _regexHandlers = new List<(Regex, string, Func<PulseContext, Task>, string[])>();
        _regexHandlersLock = new ReaderWriterLockSlim();

        _pipeline = new PulseMiddlewarePipeline();
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
        foreach (Func<PulseConnection, Task> handler in _onDisconnectHandlers)
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

    private void RegisterHandler(string handle, string version, Func<PulseContext, Task> handlerFunc)
    {
        version = version.ToLowerInvariant();

        if (!handle.Contains('{') && !handle.Contains('*') && !handle.Contains('['))
        {
            ConcurrentDictionary<string, Func<PulseContext, Task>> versionDict = _fastHandlers.GetOrAdd(version,
                _ => new ConcurrentDictionary<string, Func<PulseContext, Task>>(StringComparer.OrdinalIgnoreCase));
            versionDict[handle] = handlerFunc;
        }
        else
        {
            HandlePattern handlePattern = new HandlePattern(handle);
            string[] groupNames = handlePattern.Regex.GetGroupNames().Where(name => name != "0").ToArray();

            _regexHandlersLock.EnterWriteLock();
            try
            {
                _regexHandlers.Add((handlePattern.Regex, version, handlerFunc, groupNames));
            }
            finally
            {
                _regexHandlersLock.ExitWriteLock();
            }
        }
    }

    public void MapRpc<TResponse>(
        string handle,
        Func<PulseContext, Task<TResponse>> handlerFunc,
        string version = "v1")
    {
        RegisterHandler(handle, version, async context =>
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
        });
    }

    public void MapRpc<TRequest, TResponse>(
        string handle,
        Func<TRequest, PulseContext, Task<TResponse>> handlerFunc,
        string version = "v1")
    {
        RegisterHandler(handle, version, async context =>
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
        });
    }
    
    public void MapEvent(
        string handle,
        Func<PulseContext, Task> handlerFunc,
        string version = "v1")
    {
        RegisterHandler(handle, version, async context =>
        {
            await handlerFunc(context);
            context.TypedResponseEnvelope = null;
        });
    }

    public void MapEvent<TEvent>(
        string handle,
        Func<TEvent, PulseContext, Task> handlerFunc,
        string version = "v1")
    {
        RegisterHandler(handle, version, async context =>
        {
            PulseEnvelope<TEvent> eventEnvelope = context.GetTypedRequestEnvelope<TEvent>();
            await handlerFunc(eventEnvelope.Body, context);
            context.TypedResponseEnvelope = null;
        });
    }

    public async Task ProcessRawAsync(PulseConnection connection, byte[] rawData)
    {
        PulseEnvelope<object> envelope = MsgPackSerdes.Deserialize<PulseEnvelope<object>>(rawData);
        string handleKey = envelope.Handle;
        string versionKey = envelope.Version;
        
        Func<PulseContext, Task>? handler = null;
        Dictionary<string, string> parameters = new Dictionary<string, string>();

        if (_fastHandlers.TryGetValue(versionKey, out var versionHandlers) &&
            versionHandlers.TryGetValue(handleKey, out handler))
        {
            // exact hit
        }
        else
        {
            _regexHandlersLock.EnterReadLock();
            try
            {
                foreach ((Regex regex, string version, Func<PulseContext, Task> _handler, string[] groups) in _regexHandlers)
                {
                    if (!string.Equals(version, versionKey, StringComparison.OrdinalIgnoreCase)) continue;
                    Match match = regex.Match(handleKey);
                    if (!match.Success) continue;

                    handler = _handler;
                    foreach (string group in groups)
                        if (match.Groups[group].Success)
                            parameters[group] = match.Groups[group].Value;

                    break;
                }
            }
            finally
            {
                _regexHandlersLock.ExitReadLock();
            }
        }

        if (handler == null)
            throw new PulseException(PulseStatus.NOT_FOUND, $"Handle not found: {handleKey}");

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

        try
        {
            await _pipeline.ExecuteAsync(context, handler);

            if (context.TypedResponseEnvelope != null)
            {
                byte[] responseBytes = MsgPackSerdes.Serialize((dynamic)context.TypedResponseEnvelope);
                await connection.Socket.SendAsync(
                    new ArraySegment<byte>(responseBytes),
                    WebSocketMessageType.Binary,
                    true,
                    CancellationToken.None);
            }
        }
        catch (Exception exception)
        {
            if (envelope.Kind == PulseKind.RPC)
            {
                (PulseStatus status, string message) = ErrorMapper.MapException(exception);
                PulseEnvelope<object> errorEnvelope = new PulseEnvelope<object>
                {
                    Handle = envelope.Handle,
                    Body = message,
                    AuthToken = envelope.AuthToken,
                    Kind = PulseKind.RPC,
                    Version = envelope.Version,
                    ClientCorrelationId = envelope.ClientCorrelationId,
                    Status = status
                };

                try
                {
                    byte[] errorBytes = MsgPackSerdes.Serialize(errorEnvelope);
                    await connection.Socket.SendAsync(
                        new ArraySegment<byte>(errorBytes),
                        WebSocketMessageType.Binary,
                        true,
                        CancellationToken.None);
                }
                catch (Exception sendException)
                {
                    _logger.LogError("Failed to send error response", sendException);
                }
            }

            throw;
        }
    }


    public void Dispose()
    {
        _regexHandlersLock?.Dispose();
    }
}