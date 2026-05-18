using System.Collections.Concurrent;
using System.Net;
using System.Net.WebSockets;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Rymote.Pulse.Core;
using Rymote.Pulse.Core.Connections;
using Rymote.Pulse.Core.Logging;
using Rymote.Pulse.Core.Transport;

namespace Rymote.Pulse.Hosting;

public class PulseHostedService : BackgroundService
{
    private readonly PulseHostingOptions _options;
    private readonly PulseDispatcher _dispatcher;
    private readonly PulseConnectionManager _connectionManager;
    private readonly IPulseLogger _logger;

    private readonly SemaphoreSlim? _concurrencySemaphore;
    private readonly ConcurrentDictionary<string, Task> _activeHandlers = new();

    private HttpListener? _httpListener;

    public PulseHostedService(
        IOptions<PulseHostingOptions> options,
        PulseDispatcher dispatcher,
        PulseConnectionManager connectionManager,
        IPulseLogger logger)
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentNullException.ThrowIfNull(connectionManager);
        ArgumentNullException.ThrowIfNull(logger);

        _options = options.Value;
        _options.Validate();

        _dispatcher = dispatcher;
        _connectionManager = connectionManager;
        _logger = logger;

        if (_options.MaxConcurrentConnections is int maxConcurrentConnections)
            _concurrencySemaphore = new SemaphoreSlim(maxConcurrentConnections, maxConcurrentConnections);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _httpListener = new HttpListener
        {
            AuthenticationSchemes = _options.AuthenticationSchemes
        };

        foreach (string prefix in _options.Prefixes)
            _httpListener.Prefixes.Add(prefix);

        _httpListener.Start();
        _logger.LogInfo(
            $"Pulse hosted service listening on: {string.Join(", ", _options.Prefixes)}");

        await using CancellationTokenRegistration stopRegistration =
            stoppingToken.Register(StopListenerSafely);

        while (!stoppingToken.IsCancellationRequested)
        {
            HttpListenerContext httpListenerContext;

            try
            {
                httpListenerContext = await _httpListener.GetContextAsync();
            }
            catch (HttpListenerException)
            {
                break;
            }
            catch (ObjectDisposedException)
            {
                break;
            }
            catch (InvalidOperationException)
            {
                break;
            }

            if (!httpListenerContext.Request.IsWebSocketRequest)
            {
                _logger.LogWarning(
                    $"Rejected non-WebSocket request: {httpListenerContext.Request.RawUrl}");
                httpListenerContext.Response.StatusCode = (int)HttpStatusCode.BadRequest;
                httpListenerContext.Response.Close();
                continue;
            }

            string connectionId = Guid.NewGuid().ToString();
            Task handlerTask = Task.Run(
                () => HandleConnectionAsync(connectionId, httpListenerContext, stoppingToken),
                CancellationToken.None);

            _activeHandlers[connectionId] = handlerTask;
            _ = handlerTask.ContinueWith(
                _ => _activeHandlers.TryRemove(connectionId, out Task? _),
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInfo("Pulse hosted service stopping...");

        StopListenerSafely();

        foreach (string connectionId in await _connectionManager.GetAllConnectionIdsAsync())
        {
            try
            {
                await _connectionManager.DisconnectAsync(
                    connectionId,
                    WebSocketCloseStatus.EndpointUnavailable,
                    "Server shutting down",
                    cancellationToken);
            }
            catch (Exception disconnectException)
            {
                _logger.LogDebug(
                    $"Error disconnecting {connectionId} during shutdown: {disconnectException.Message}");
            }
        }

        using CancellationTokenSource drainCancellationTokenSource =
            CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        drainCancellationTokenSource.CancelAfter(_options.ShutdownDrainTimeout);

        try
        {
            await Task.WhenAll(_activeHandlers.Values).WaitAsync(drainCancellationTokenSource.Token);
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning(
                $"{_activeHandlers.Count} connection handler(s) did not drain within {_options.ShutdownDrainTimeout}.");
        }
        catch (Exception drainException)
        {
            _logger.LogDebug($"Drain error: {drainException.Message}");
        }

        CloseListenerSafely();

        await base.StopAsync(cancellationToken);
    }

    private async Task HandleConnectionAsync(
        string connectionId,
        HttpListenerContext httpListenerContext,
        CancellationToken cancellationToken)
    {
        bool concurrencySemaphoreAcquired = false;
        PulseConnection? connection = null;
        string ipAddress = GetClientIpAddress(httpListenerContext);

        try
        {
            if (_concurrencySemaphore != null)
            {
                concurrencySemaphoreAcquired = await _concurrencySemaphore.WaitAsync(0, cancellationToken);
                if (!concurrencySemaphoreAcquired)
                {
                    _logger.LogWarning($"[{connectionId}] Rejected: concurrency limit reached");
                    try
                    {
                        HttpListenerWebSocketContext rejectContext =
                            await httpListenerContext.AcceptWebSocketAsync(subProtocol: null);
                        await rejectContext.WebSocket.CloseAsync(
                            WebSocketCloseStatus.PolicyViolation,
                            "Server at capacity",
                            CancellationToken.None);
                    }
                    catch (Exception rejectException)
                    {
                        _logger.LogDebug(
                            $"[{connectionId}] Error closing over-capacity socket: {rejectException.Message}");
                    }

                    return;
                }
            }

            string userAgent = httpListenerContext.Request.UserAgent ?? "Unknown";
            string origin = httpListenerContext.Request.Headers["Origin"] ?? "Unknown";
            string? forwardedFor = httpListenerContext.Request.Headers["X-Forwarded-For"];

            HttpListenerWebSocketContext webSocketContext =
                await httpListenerContext.AcceptWebSocketAsync(subProtocol: null);

            Dictionary<string, string> queryParameters = new Dictionary<string, string>();
            foreach (string? queryParameterKey in httpListenerContext.Request.QueryString.AllKeys)
            {
                if (queryParameterKey == null) continue;
                queryParameters[queryParameterKey] =
                    httpListenerContext.Request.QueryString[queryParameterKey] ?? string.Empty;
            }

            connection = await _connectionManager.AddConnectionAsync(
                connectionId, webSocketContext.WebSocket, queryParameters);

            connection.SetMetadata("http_listener_context", httpListenerContext);
            connection.SetMetadata("ip_address", ipAddress);
            connection.SetMetadata("user_agent", userAgent);
            connection.SetMetadata("origin", origin);
            connection.SetMetadata("connected_at", DateTime.UtcNow);

            if (_options.AuthenticationSchemes != AuthenticationSchemes.Anonymous
                && httpListenerContext.User != null)
            {
                connection.SetMetadata("user_principal", httpListenerContext.User);
            }

            _logger.LogInfo(
                $"[{connectionId}] Client connected: IP: {ipAddress} | Origin: {origin} | UserAgent: {userAgent}");

            if (!string.IsNullOrEmpty(forwardedFor))
                _logger.LogDebug($"[{connectionId}] Connection forwarded through: {forwardedFor}");

            try
            {
                await _dispatcher.ExecuteOnConnectHandlersAsync(connection);
            }
            catch (Exception onConnectException)
            {
                _logger.LogError(
                    $"[{connectionId}] Error in OnConnect handler",
                    onConnectException);

                await _connectionManager.DisconnectAsync(
                    connection,
                    WebSocketCloseStatus.PolicyViolation,
                    onConnectException.Message);

                return;
            }

            try
            {
                await PulseSocketLoop.RunAsync(
                    connection,
                    _dispatcher,
                    _logger,
                    _options,
                    cancellationToken);
            }
            catch (Exception loopException)
            {
                _logger.LogError(
                    $"[{connectionId}] Unhandled exception in Pulse socket loop",
                    loopException);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception unexpectedException)
        {
            _logger.LogError(
                $"[{connectionId}] Unexpected error handling connection",
                unexpectedException);
        }
        finally
        {
            if (connection != null)
            {
                try
                {
                    await _dispatcher.ExecuteOnDisconnectHandlersAsync(connection);
                }
                catch (Exception onDisconnectException)
                {
                    _logger.LogError(
                        $"[{connectionId}] Error in OnDisconnect handler",
                        onDisconnectException);
                }

                bool connectedAtExists = connection.Metadata.TryGet("connected_at", out DateTime connectedAt);
                TimeSpan connectedDuration =
                    DateTime.UtcNow - (connectedAtExists ? connectedAt : DateTime.UtcNow);

                await _connectionManager.RemoveConnectionAsync(connectionId);

                string durationText = connectedDuration.TotalHours >= 24
                    ? connectedDuration.ToString(@"d\.hh\:mm\:ss")
                    : connectedDuration.ToString(@"hh\:mm\:ss");

                _logger.LogInfo(
                    $"[{connectionId}] Client disconnected: IP: {ipAddress} | Duration: {durationText}");
            }

            if (concurrencySemaphoreAcquired)
                _concurrencySemaphore?.Release();
        }
    }

    private void StopListenerSafely()
    {
        try
        {
            _httpListener?.Stop();
        }
        catch (Exception stopException)
        {
            _logger.LogDebug($"Error stopping HttpListener: {stopException.Message}");
        }
    }

    private void CloseListenerSafely()
    {
        try
        {
            _httpListener?.Close();
        }
        catch (Exception closeException)
        {
            _logger.LogDebug($"Error closing HttpListener: {closeException.Message}");
        }
    }

    private static string GetClientIpAddress(HttpListenerContext httpListenerContext)
    {
        string? forwardedFor = httpListenerContext.Request.Headers["X-Forwarded-For"];
        if (!string.IsNullOrEmpty(forwardedFor))
            return forwardedFor.Split(',')[0].Trim();

        string? realIp = httpListenerContext.Request.Headers["X-Real-IP"];
        if (!string.IsNullOrEmpty(realIp))
            return realIp;

        return httpListenerContext.Request.RemoteEndPoint?.Address.ToString() ?? "Unknown";
    }

    public override void Dispose()
    {
        _concurrencySemaphore?.Dispose();
        ((IDisposable?)_httpListener)?.Dispose();
        base.Dispose();
        GC.SuppressFinalize(this);
    }
}
