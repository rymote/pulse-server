using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using Rymote.Pulse.Core.Connections;
using Rymote.Pulse.Core.Exceptions;
using Rymote.Pulse.Core.Logging;
using Rymote.Pulse.Core.Messages;
using Rymote.Pulse.Core.Middleware;
using Rymote.Pulse.Core.Serialization;
using Rymote.Pulse.Core.Transport;

namespace Rymote.Pulse.Core;

public class PulseDispatcher : IDisposable
{
    public readonly PulseConnectionManager ConnectionManager;

    private readonly List<Func<PulseConnection, Task>> _onConnectHandlers = [];
    private readonly List<Func<PulseConnection, Task>> _onDisconnectHandlers = [];

    private readonly IPulseLogger _logger;
    private readonly PulseMiddlewarePipeline _pipeline;

    // RPC handlers return pre-serialized envelope bytes so the concrete TResponse is known at serialize time.
    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, Func<PulseContext, Task<byte[]>>>>
        _rpcHandlers;

    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, Func<PulseContext, IAsyncEnumerable<byte[]>>>>
        _rpcStreamHandlers;

    private readonly ConcurrentDictionary<string, ConcurrentDictionary<string, Func<PulseContext, Task>>>
        _eventHandlers;

    private readonly List<(Regex CompiledRegex, string Version, Func<PulseContext, Task<byte[]>> Handler, string[] GroupNames)>
        _regexRpcHandlers;

    private readonly List<(Regex CompiledRegex, string Version, Func<PulseContext, IAsyncEnumerable<byte[]>> Handler, string[] GroupNames)>
        _regexRpcStreamHandlers;

    private readonly List<(Regex CompiledRegex, string Version, Func<PulseContext, Task> Handler, string[] GroupNames)>
        _regexEventHandlers;

    private readonly ReaderWriterLockSlim _regexHandlersLock;

    public PulseDispatcher(PulseConnectionManager connectionManager, IPulseLogger logger)
    {
        ConnectionManager = connectionManager;
        _logger = logger;

        _rpcHandlers = new(StringComparer.OrdinalIgnoreCase);
        _rpcStreamHandlers = new(StringComparer.OrdinalIgnoreCase);
        _eventHandlers = new(StringComparer.OrdinalIgnoreCase);

        _regexRpcHandlers = [];
        _regexRpcStreamHandlers = [];
        _regexEventHandlers = [];

        _regexHandlersLock = new ReaderWriterLockSlim();
        _pipeline = new PulseMiddlewarePipeline();
    }

    public void Use(PulseMiddlewareDelegate middleware) => _pipeline.Use(middleware);

    public void AddOnConnectHandler(Func<PulseConnection, Task> handler) => _onConnectHandlers.Add(handler);

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
                _logger.LogError("Error in OnConnect handler", exception);
                throw;
            }
        }
    }

    public void AddOnDisconnectHandler(Func<PulseConnection, Task> handler) => _onDisconnectHandlers.Add(handler);

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
                _logger.LogError("Error in OnDisconnect handler", exception);
            }
        }
    }

    public void MapRpc<TResponse>(
        string handle,
        Func<PulseContext, Task<TResponse>> handlerFunc,
        string version = "v1")
    {
        RegisterRpcHandler(handle, version, async context =>
        {
            TResponse responseBody = await handlerFunc(context);
            PulseEnvelope<TResponse> typedEnvelope = new PulseEnvelope<TResponse>
            {
                Handle = context.UntypedRequest.Handle,
                Body = responseBody!,
                AuthToken = context.UntypedRequest.AuthToken,
                Kind = PulseKind.RPC,
                Version = context.UntypedRequest.Version,
                Status = PulseStatus.OK
            };
            return MsgPackSerdes.Serialize(typedEnvelope);
        });
    }

    public void MapRpc<TRequest, TResponse>(
        string handle,
        Func<TRequest, PulseContext, Task<TResponse>> handlerFunc,
        string version = "v1")
    {
        RegisterRpcHandler(handle, version, async context =>
        {
            PulseEnvelope<TRequest> requestEnvelope = context.GetTypedRequestEnvelope<TRequest>();
            TResponse responseBody = await handlerFunc(requestEnvelope.Body, context);
            PulseEnvelope<TResponse> typedEnvelope = new PulseEnvelope<TResponse>
            {
                Handle = requestEnvelope.Handle,
                Body = responseBody!,
                AuthToken = requestEnvelope.AuthToken,
                Kind = PulseKind.RPC,
                Version = requestEnvelope.Version,
                Status = PulseStatus.OK
            };
            return MsgPackSerdes.Serialize(typedEnvelope);
        });
    }

    public void MapRpcStream<TRequest, TResponse>(
        string handle,
        Func<TRequest, PulseContext, IAsyncEnumerable<TResponse>> handlerFunc,
        string version = "v1")
    {
        RegisterRpcStreamHandler(handle, version, context =>
            WrapRpcStreamHandlerAsync(handlerFunc, context));
    }

    private static async IAsyncEnumerable<byte[]> WrapRpcStreamHandlerAsync<TRequest, TResponse>(
        Func<TRequest, PulseContext, IAsyncEnumerable<TResponse>> handlerFunc,
        PulseContext context)
    {
        PulseEnvelope<TRequest> requestEnvelope = context.GetTypedRequestEnvelope<TRequest>();

        await foreach (TResponse responseItem in
            handlerFunc(requestEnvelope.Body, context).WithCancellation(context.CancellationToken))
        {
            PulseEnvelope<TResponse> typedEnvelope = new PulseEnvelope<TResponse>
            {
                Handle = requestEnvelope.Handle,
                Body = responseItem!,
                AuthToken = requestEnvelope.AuthToken,
                Kind = PulseKind.RPC_STREAM,
                Version = requestEnvelope.Version,
                Status = PulseStatus.OK
            };
            yield return MsgPackSerdes.Serialize(typedEnvelope);
        }
    }

    public void MapEvent(
        string handle,
        Func<PulseContext, Task> handlerFunc,
        string version = "v1")
    {
        RegisterEventHandler(handle, version, handlerFunc);
    }

    public void MapEvent<TEvent>(
        string handle,
        Func<TEvent, PulseContext, Task> handlerFunc,
        string version = "v1")
    {
        RegisterEventHandler(handle, version, async context =>
        {
            PulseEnvelope<TEvent> eventEnvelope = context.GetTypedRequestEnvelope<TEvent>();
            await handlerFunc(eventEnvelope.Body, context);
        });
    }

    public async Task ProcessStreamAsync(
        IPulseStream stream,
        PulseConnection connection,
        CancellationToken cancellationToken)
    {
        ReadOnlyMemory<byte>? firstEnvelopeBytes;
        try
        {
            firstEnvelopeBytes = await stream.ReadEnvelopeAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (PulseStreamResetException)
        {
            return;
        }

        if (firstEnvelopeBytes is null)
        {
            await stream.AbortAsync(reasonCode: 2, CancellationToken.None).ConfigureAwait(false);
            return;
        }

        byte[] rawBytes = firstEnvelopeBytes.Value.ToArray();
        PulseEnvelope<object> envelope;

        try
        {
            envelope = MsgPackSerdes.Deserialize<PulseEnvelope<object>>(rawBytes);
        }
        catch
        {
            await stream.AbortAsync(reasonCode: 4, CancellationToken.None).ConfigureAwait(false);
            return;
        }

        switch (envelope.Kind)
        {
            case PulseKind.RPC:
                await ProcessRpcAsync(stream, connection, envelope, rawBytes, cancellationToken).ConfigureAwait(false);
                break;
            case PulseKind.RPC_STREAM:
                await ProcessRpcStreamAsync(stream, connection, envelope, rawBytes, cancellationToken).ConfigureAwait(false);
                break;
            case PulseKind.EVENT:
                await ProcessEventStreamAsync(stream, connection, envelope, rawBytes, cancellationToken).ConfigureAwait(false);
                break;
            default:
                await stream.AbortAsync(reasonCode: 6, CancellationToken.None).ConfigureAwait(false);
                break;
        }
    }

    public async Task ProcessDatagramAsync(
        ReadOnlyMemory<byte> envelopeBytes,
        PulseConnection connection,
        CancellationToken cancellationToken)
    {
        byte[] rawBytes = envelopeBytes.ToArray();
        PulseEnvelope<object> envelope;

        try
        {
            envelope = MsgPackSerdes.Deserialize<PulseEnvelope<object>>(rawBytes);
        }
        catch (Exception decodeException)
        {
            _logger.LogWarning($"Dropped datagram with undecodable envelope: {decodeException.Message}");
            return;
        }

        if (envelope.Kind != PulseKind.DATAGRAM_EVENT && envelope.Kind != PulseKind.EVENT)
        {
            _logger.LogWarning($"Dropped datagram with unsupported kind: {envelope.Kind}");
            return;
        }

        await RunEventHandlerAsync(connection, envelope, rawBytes, cancellationToken).ConfigureAwait(false);
    }

    private async Task ProcessRpcAsync(
        IPulseStream stream,
        PulseConnection connection,
        PulseEnvelope<object> envelope,
        byte[] rawBytes,
        CancellationToken cancellationToken)
    {
        Dictionary<string, string> parameters = new();
        Func<PulseContext, Task<byte[]>>? handler =
            LookupRpcHandler(envelope.Handle, envelope.Version, parameters);

        if (handler == null)
        {
            await WriteErrorEnvelopeAsync(stream, envelope, PulseStatus.NOT_FOUND,
                $"Handle not found: {envelope.Handle}", PulseKind.RPC, cancellationToken).ConfigureAwait(false);
            return;
        }

        PulseContext context = new PulseContext(
            ConnectionManager, connection, rawBytes, _logger, parameters, cancellationToken);

        byte[]? capturedResponseBytes = null;
        Exception? handlerException = null;

        try
        {
            await _pipeline.ExecuteAsync(context, async pipelineContext =>
            {
                capturedResponseBytes = await handler(pipelineContext).ConfigureAwait(false);
            }).ConfigureAwait(false);
        }
        catch (Exception thrown)
        {
            handlerException = thrown;
        }

        if (handlerException != null)
        {
            (PulseStatus status, string message) = ErrorMapper.MapException(handlerException);
            await WriteErrorEnvelopeAsync(stream, envelope, status, message, PulseKind.RPC, cancellationToken)
                .ConfigureAwait(false);
            return;
        }

        if (capturedResponseBytes == null)
        {
            await WriteErrorEnvelopeAsync(stream, envelope, PulseStatus.INTERNAL_ERROR,
                "Handler completed without producing a response.", PulseKind.RPC, cancellationToken).ConfigureAwait(false);
            return;
        }

        try
        {
            await stream.WriteEnvelopeAsync(capturedResponseBytes, cancellationToken).ConfigureAwait(false);
            await stream.CompleteWritesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception writeException)
        {
            _logger.LogDebug(
                $"[{connection.ConnectionId}] Failed to write RPC response: {writeException.Message}");
        }
    }

    private async Task ProcessRpcStreamAsync(
        IPulseStream stream,
        PulseConnection connection,
        PulseEnvelope<object> envelope,
        byte[] rawBytes,
        CancellationToken cancellationToken)
    {
        Dictionary<string, string> parameters = new();
        Func<PulseContext, IAsyncEnumerable<byte[]>>? handler =
            LookupRpcStreamHandler(envelope.Handle, envelope.Version, parameters);

        if (handler == null)
        {
            await WriteErrorEnvelopeAsync(stream, envelope, PulseStatus.NOT_FOUND,
                $"Handle not found: {envelope.Handle}", PulseKind.RPC_STREAM, cancellationToken).ConfigureAwait(false);
            return;
        }

        PulseContext context = new PulseContext(
            ConnectionManager, connection, rawBytes, _logger, parameters, cancellationToken);

        try
        {
            IAsyncEnumerable<byte[]>? envelopeBytesStream = null;
            await _pipeline.ExecuteAsync(context, pipelineContext =>
            {
                envelopeBytesStream = handler(pipelineContext);
                return Task.CompletedTask;
            }).ConfigureAwait(false);

            if (envelopeBytesStream != null)
            {
                await foreach (byte[] responseEnvelopeBytes in
                    envelopeBytesStream.WithCancellation(cancellationToken).ConfigureAwait(false))
                {
                    await stream.WriteEnvelopeAsync(responseEnvelopeBytes, cancellationToken).ConfigureAwait(false);
                }
            }

            await stream.CompleteWritesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception streamException)
        {
            (PulseStatus status, string message) = ErrorMapper.MapException(streamException);
            await WriteErrorEnvelopeAsync(stream, envelope, status, message, PulseKind.RPC_STREAM,
                CancellationToken.None).ConfigureAwait(false);
        }
    }

    private async Task ProcessEventStreamAsync(
        IPulseStream stream,
        PulseConnection connection,
        PulseEnvelope<object> envelope,
        byte[] rawBytes,
        CancellationToken cancellationToken)
    {
        await RunEventHandlerAsync(connection, envelope, rawBytes, cancellationToken).ConfigureAwait(false);

        try
        {
            await stream.CompleteWritesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // event streams are uni; completing writes on the read side is best-effort
        }
    }

    private async Task RunEventHandlerAsync(
        PulseConnection connection,
        PulseEnvelope<object> envelope,
        byte[] rawBytes,
        CancellationToken cancellationToken)
    {
        Dictionary<string, string> parameters = new();
        Func<PulseContext, Task>? handler =
            LookupEventHandler(envelope.Handle, envelope.Version, parameters);

        if (handler == null)
        {
            _logger.LogDebug($"No event handler registered for handle '{envelope.Handle}' (version {envelope.Version})");
            return;
        }

        PulseContext context = new PulseContext(
            ConnectionManager, connection, rawBytes, _logger, parameters, cancellationToken);

        try
        {
            await _pipeline.ExecuteAsync(context, handler).ConfigureAwait(false);
        }
        catch (Exception eventException)
        {
            _logger.LogError($"[{connection.ConnectionId}] Event handler '{envelope.Handle}' threw", eventException);
        }
    }

    private async Task WriteErrorEnvelopeAsync(
        IPulseStream stream,
        PulseEnvelope<object> requestEnvelope,
        PulseStatus status,
        string error,
        PulseKind kind,
        CancellationToken cancellationToken)
    {
        PulseEnvelope<string> errorEnvelope = new PulseEnvelope<string>
        {
            Handle = requestEnvelope.Handle,
            Body = error,
            AuthToken = requestEnvelope.AuthToken,
            Kind = kind,
            Version = requestEnvelope.Version,
            Status = status,
            Error = error
        };

        try
        {
            await stream.WriteEnvelopeAsync(MsgPackSerdes.Serialize(errorEnvelope), cancellationToken).ConfigureAwait(false);
            await stream.CompleteWritesAsync(cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // best-effort
        }
    }

    private void RegisterRpcHandler(string handle, string version,
        Func<PulseContext, Task<byte[]>> handlerFunc)
    {
        version = version.ToLowerInvariant();

        if (IsLiteralHandle(handle))
        {
            ConcurrentDictionary<string, Func<PulseContext, Task<byte[]>>> versionDict =
                _rpcHandlers.GetOrAdd(version, _ => new ConcurrentDictionary<string, Func<PulseContext, Task<byte[]>>>(StringComparer.OrdinalIgnoreCase));
            versionDict[handle] = handlerFunc;
        }
        else
        {
            HandlePattern handlePattern = new HandlePattern(handle);
            string[] groupNames = handlePattern.Regex.GetGroupNames().Where(name => name != "0").ToArray();

            _regexHandlersLock.EnterWriteLock();
            try { _regexRpcHandlers.Add((handlePattern.Regex, version, handlerFunc, groupNames)); }
            finally { _regexHandlersLock.ExitWriteLock(); }
        }
    }

    private void RegisterRpcStreamHandler(string handle, string version,
        Func<PulseContext, IAsyncEnumerable<byte[]>> handlerFunc)
    {
        version = version.ToLowerInvariant();

        if (IsLiteralHandle(handle))
        {
            ConcurrentDictionary<string, Func<PulseContext, IAsyncEnumerable<byte[]>>> versionDict =
                _rpcStreamHandlers.GetOrAdd(version, _ => new ConcurrentDictionary<string, Func<PulseContext, IAsyncEnumerable<byte[]>>>(StringComparer.OrdinalIgnoreCase));
            versionDict[handle] = handlerFunc;
        }
        else
        {
            HandlePattern handlePattern = new HandlePattern(handle);
            string[] groupNames = handlePattern.Regex.GetGroupNames().Where(name => name != "0").ToArray();

            _regexHandlersLock.EnterWriteLock();
            try { _regexRpcStreamHandlers.Add((handlePattern.Regex, version, handlerFunc, groupNames)); }
            finally { _regexHandlersLock.ExitWriteLock(); }
        }
    }

    private void RegisterEventHandler(string handle, string version, Func<PulseContext, Task> handlerFunc)
    {
        version = version.ToLowerInvariant();

        if (IsLiteralHandle(handle))
        {
            ConcurrentDictionary<string, Func<PulseContext, Task>> versionDict =
                _eventHandlers.GetOrAdd(version, _ => new ConcurrentDictionary<string, Func<PulseContext, Task>>(StringComparer.OrdinalIgnoreCase));
            versionDict[handle] = handlerFunc;
        }
        else
        {
            HandlePattern handlePattern = new HandlePattern(handle);
            string[] groupNames = handlePattern.Regex.GetGroupNames().Where(name => name != "0").ToArray();

            _regexHandlersLock.EnterWriteLock();
            try { _regexEventHandlers.Add((handlePattern.Regex, version, handlerFunc, groupNames)); }
            finally { _regexHandlersLock.ExitWriteLock(); }
        }
    }

    private static bool IsLiteralHandle(string handle)
        => !handle.Contains('{') && !handle.Contains('*') && !handle.Contains('[');

    private Func<PulseContext, Task<byte[]>>? LookupRpcHandler(
        string handle, string version, Dictionary<string, string> parameters)
    {
        if (_rpcHandlers.TryGetValue(version, out ConcurrentDictionary<string, Func<PulseContext, Task<byte[]>>>? versionDict) &&
            versionDict.TryGetValue(handle, out Func<PulseContext, Task<byte[]>>? exact))
            return exact;

        _regexHandlersLock.EnterReadLock();
        try
        {
            foreach ((Regex regex, string entryVersion, Func<PulseContext, Task<byte[]>> handler, string[] groups)
                in _regexRpcHandlers)
            {
                if (!string.Equals(entryVersion, version, StringComparison.OrdinalIgnoreCase)) continue;
                Match match = regex.Match(handle);
                if (!match.Success) continue;

                foreach (string groupName in groups)
                    if (match.Groups[groupName].Success)
                        parameters[groupName] = match.Groups[groupName].Value;

                return handler;
            }
        }
        finally
        {
            _regexHandlersLock.ExitReadLock();
        }

        return null;
    }

    private Func<PulseContext, IAsyncEnumerable<byte[]>>? LookupRpcStreamHandler(
        string handle, string version, Dictionary<string, string> parameters)
    {
        if (_rpcStreamHandlers.TryGetValue(version, out ConcurrentDictionary<string, Func<PulseContext, IAsyncEnumerable<byte[]>>>? versionDict) &&
            versionDict.TryGetValue(handle, out Func<PulseContext, IAsyncEnumerable<byte[]>>? exact))
            return exact;

        _regexHandlersLock.EnterReadLock();
        try
        {
            foreach ((Regex regex, string entryVersion, Func<PulseContext, IAsyncEnumerable<byte[]>> handler, string[] groups)
                in _regexRpcStreamHandlers)
            {
                if (!string.Equals(entryVersion, version, StringComparison.OrdinalIgnoreCase)) continue;
                Match match = regex.Match(handle);
                if (!match.Success) continue;

                foreach (string groupName in groups)
                    if (match.Groups[groupName].Success)
                        parameters[groupName] = match.Groups[groupName].Value;

                return handler;
            }
        }
        finally
        {
            _regexHandlersLock.ExitReadLock();
        }

        return null;
    }

    private Func<PulseContext, Task>? LookupEventHandler(
        string handle, string version, Dictionary<string, string> parameters)
    {
        if (_eventHandlers.TryGetValue(version, out ConcurrentDictionary<string, Func<PulseContext, Task>>? versionDict) &&
            versionDict.TryGetValue(handle, out Func<PulseContext, Task>? exact))
            return exact;

        _regexHandlersLock.EnterReadLock();
        try
        {
            foreach ((Regex regex, string entryVersion, Func<PulseContext, Task> handler, string[] groups)
                in _regexEventHandlers)
            {
                if (!string.Equals(entryVersion, version, StringComparison.OrdinalIgnoreCase)) continue;
                Match match = regex.Match(handle);
                if (!match.Success) continue;

                foreach (string groupName in groups)
                    if (match.Groups[groupName].Success)
                        parameters[groupName] = match.Groups[groupName].Value;

                return handler;
            }
        }
        finally
        {
            _regexHandlersLock.ExitReadLock();
        }

        return null;
    }

    public void Dispose()
    {
        _regexHandlersLock?.Dispose();
    }
}
