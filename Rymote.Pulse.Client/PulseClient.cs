using System.Runtime.CompilerServices;
using Rymote.Pulse.Core;
using Rymote.Pulse.Core.Exceptions;
using Rymote.Pulse.Core.Logging;
using Rymote.Pulse.Core.Messages;
using Rymote.Pulse.Core.Serialization;
using Rymote.Pulse.Core.Transport;

namespace Rymote.Pulse.Client;

public class PulseClient : IAsyncDisposable
{
    private readonly PulseClientOptions _options;
    private readonly IPulseLogger _logger;
    private readonly PulseClientEventHandlerRegistry _eventHandlerRegistry = new();

    private IPulseSession? _session;
    private IPulseClientTransport? _activeTransport;
    private CancellationTokenSource? _sessionCancellationTokenSource;
    private Task? _sessionPipelineTask;
    private int _disposed;
    private int _reconnectAttempts;

    public bool IsConnected => _session != null && _session.IsOpen;
    public string? ActiveTransportName => _activeTransport?.Name;

    public PulseClient(PulseClientOptions options, IPulseLogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (options.Transports == null || options.Transports.Count == 0)
            throw new ArgumentException("At least one transport is required.", nameof(options));

        _options = options;
        _logger = logger ?? new PulseConsoleLogger(enableDebugLogs: false);
    }

    public async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        Exception? lastException = null;

        foreach (IPulseClientTransport transport in _options.Transports)
        {
            try
            {
                using CancellationTokenSource connectTimeoutCts = new CancellationTokenSource(_options.ConnectTimeout);
                using CancellationTokenSource linkedCts =
                    CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, connectTimeoutCts.Token);

                _logger.LogInfo($"Pulse client: trying transport '{transport.Name}'...");

                _session = await transport.ConnectAsync(
                    _options.Endpoint, _options.AuthToken, _options.QueryParameters, linkedCts.Token)
                    .ConfigureAwait(false);

                _activeTransport = transport;
                _sessionCancellationTokenSource = new CancellationTokenSource();
                _sessionPipelineTask = Task.Run(() =>
                    RunSessionLoopAsync(_session, _sessionCancellationTokenSource.Token), CancellationToken.None);

                _logger.LogInfo($"Pulse client connected via '{transport.Name}'");
                _reconnectAttempts = 0;
                return;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception transportException)
            {
                lastException = transportException;
                _logger.LogDebug($"Pulse client: transport '{transport.Name}' failed: {transportException.Message}");
            }
        }

        throw new PulseConnectException("All Pulse client transports failed to connect.", lastException);
    }

    public async Task DisconnectAsync(CancellationToken cancellationToken = default)
    {
        _sessionCancellationTokenSource?.Cancel();

        if (_session != null)
        {
            try { await _session.CloseAsync(reasonCode: 1000, drainTimeout: TimeSpan.Zero, cancellationToken); }
            catch { /* best-effort */ }
            try { await _session.DisposeAsync(); }
            catch { /* best-effort */ }
        }

        if (_sessionPipelineTask != null)
        {
            try { await _sessionPipelineTask.ConfigureAwait(false); }
            catch { /* shutdown */ }
        }

        _session = null;
        _activeTransport = null;
    }

    public async Task<TResponse> InvokeAsync<TRequest, TResponse>(
        string handle,
        TRequest payload,
        string version = "v1",
        CancellationToken cancellationToken = default)
    {
        IPulseSession session = RequireSession();

        PulseEnvelope<TRequest> requestEnvelope = new PulseEnvelope<TRequest>
        {
            Handle = handle,
            Body = payload!,
            AuthToken = _options.AuthToken,
            Kind = PulseKind.RPC,
            Version = version
        };
        byte[] requestBytes = MsgPackSerdes.Serialize(requestEnvelope);

        IPulseStream stream = await session.OpenStreamAsync(
            PulseStreamDirection.Bidirectional, cancellationToken).ConfigureAwait(false);

        try
        {
            await stream.WriteEnvelopeAsync(requestBytes, cancellationToken).ConfigureAwait(false);
            await stream.CompleteWritesAsync(cancellationToken).ConfigureAwait(false);

            using CancellationTokenSource requestTimeoutCts = new CancellationTokenSource(_options.RequestTimeout);
            using CancellationTokenSource linkedCts =
                CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, requestTimeoutCts.Token);

            ReadOnlyMemory<byte>? responseBytes = await stream.ReadEnvelopeAsync(linkedCts.Token).ConfigureAwait(false);
            if (responseBytes is null)
                throw new PulseException(PulseStatus.INTERNAL_ERROR, "Server closed stream without sending a response.");

            PulseEnvelope<TResponse> responseEnvelope =
                MsgPackSerdes.Deserialize<PulseEnvelope<TResponse>>(responseBytes.Value.ToArray());

            if (responseEnvelope.Status.HasValue && responseEnvelope.Status.Value != PulseStatus.OK)
                throw new PulseException(responseEnvelope.Status.Value, responseEnvelope.Error ?? "RPC error");

            return responseEnvelope.Body!;
        }
        finally
        {
            await stream.DisposeAsync().ConfigureAwait(false);
        }
    }

    public async IAsyncEnumerable<TResponse> InvokeStreamAsync<TRequest, TResponse>(
        string handle,
        TRequest payload,
        string version = "v1",
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        IPulseSession session = RequireSession();

        PulseEnvelope<TRequest> requestEnvelope = new PulseEnvelope<TRequest>
        {
            Handle = handle,
            Body = payload!,
            AuthToken = _options.AuthToken,
            Kind = PulseKind.RPC_STREAM,
            Version = version
        };
        byte[] requestBytes = MsgPackSerdes.Serialize(requestEnvelope);

        IPulseStream stream = await session.OpenStreamAsync(
            PulseStreamDirection.Bidirectional, cancellationToken).ConfigureAwait(false);

        try
        {
            await stream.WriteEnvelopeAsync(requestBytes, cancellationToken).ConfigureAwait(false);
            await stream.CompleteWritesAsync(cancellationToken).ConfigureAwait(false);

            while (true)
            {
                ReadOnlyMemory<byte>? envelopeBytes;
                try
                {
                    envelopeBytes = await stream.ReadEnvelopeAsync(cancellationToken).ConfigureAwait(false);
                }
                catch (PulseStreamResetException) { yield break; }

                if (envelopeBytes is null) yield break;

                PulseEnvelope<TResponse> responseEnvelope =
                    MsgPackSerdes.Deserialize<PulseEnvelope<TResponse>>(envelopeBytes.Value.ToArray());

                if (responseEnvelope.Status.HasValue && responseEnvelope.Status.Value != PulseStatus.OK)
                    throw new PulseException(responseEnvelope.Status.Value, responseEnvelope.Error ?? "Stream error");

                yield return responseEnvelope.Body!;
            }
        }
        finally
        {
            await stream.DisposeAsync().ConfigureAwait(false);
        }
    }

    public async Task SendEventAsync<TPayload>(
        string handle,
        TPayload payload,
        string version = "v1",
        PulseDeliveryMode deliveryMode = PulseDeliveryMode.Reliable,
        CancellationToken cancellationToken = default)
    {
        IPulseSession session = RequireSession();

        PulseEnvelope<TPayload> envelope = new PulseEnvelope<TPayload>
        {
            Handle = handle,
            Body = payload,
            AuthToken = _options.AuthToken,
            Kind = deliveryMode == PulseDeliveryMode.Datagram ? PulseKind.DATAGRAM_EVENT : PulseKind.EVENT,
            Version = version
        };
        byte[] envelopeBytes = MsgPackSerdes.Serialize(envelope);

        if (deliveryMode == PulseDeliveryMode.Datagram)
        {
            if (session.Datagrams == null)
                throw new NotSupportedException(
                    $"Transport '{session.TransportName}' does not support datagrams.");
            await session.Datagrams.SendDatagramAsync(envelopeBytes, cancellationToken).ConfigureAwait(false);
            return;
        }

        IPulseStream stream = await session.OpenStreamAsync(
            PulseStreamDirection.UnidirectionalClientToServer, cancellationToken).ConfigureAwait(false);

        try
        {
            await stream.WriteEnvelopeAsync(envelopeBytes, cancellationToken).ConfigureAwait(false);
            await stream.CompleteWritesAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            await stream.DisposeAsync().ConfigureAwait(false);
        }
    }

    public void On<TEvent>(string handlePattern, Func<TEvent, PulseClientEventContext, Task> handler)
    {
        ArgumentException.ThrowIfNullOrEmpty(handlePattern);
        ArgumentNullException.ThrowIfNull(handler);
        _eventHandlerRegistry.Register(handlePattern, handler);
    }

    private async Task RunSessionLoopAsync(IPulseSession session, CancellationToken cancellationToken)
    {
        try
        {
            await PulseClientSessionPipeline.RunAsync(session, _eventHandlerRegistry, _logger, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (Exception sessionLoopException)
        {
            _logger.LogError("Pulse client session loop error", sessionLoopException);
        }

        if (_options.AutoReconnect && !cancellationToken.IsCancellationRequested && _disposed == 0)
            _ = Task.Run(() => ReconnectAsync(CancellationToken.None));
    }

    private async Task ReconnectAsync(CancellationToken cancellationToken)
    {
        TimeSpan delay = _options.ReconnectInitialDelay;

        while (!cancellationToken.IsCancellationRequested && _disposed == 0)
        {
            _reconnectAttempts++;
            _logger.LogInfo($"Pulse client reconnect attempt #{_reconnectAttempts} in {delay.TotalMilliseconds:0}ms");

            try { await Task.Delay(delay, cancellationToken).ConfigureAwait(false); }
            catch (OperationCanceledException) { return; }

            try
            {
                await ConnectAsync(cancellationToken).ConfigureAwait(false);
                return;
            }
            catch (PulseConnectException)
            {
                delay = TimeSpan.FromMilliseconds(
                    Math.Min(delay.TotalMilliseconds * 2, _options.ReconnectMaxDelay.TotalMilliseconds));
            }
        }
    }

    private IPulseSession RequireSession()
    {
        IPulseSession? session = _session;
        if (session == null || !session.IsOpen)
            throw new InvalidOperationException("Pulse client is not connected. Call ConnectAsync first.");
        return session;
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

        await DisconnectAsync().ConfigureAwait(false);
        _sessionCancellationTokenSource?.Dispose();
        GC.SuppressFinalize(this);
    }
}
