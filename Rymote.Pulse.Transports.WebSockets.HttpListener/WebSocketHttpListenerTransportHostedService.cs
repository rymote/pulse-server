using System.Collections.Concurrent;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Rymote.Pulse.Core;
using Rymote.Pulse.Core.Logging;
using Rymote.Pulse.Core.Transport;

namespace Rymote.Pulse.Transports.WebSockets.HttpListener;

internal sealed class WebSocketHttpListenerTransportHostedService : BackgroundService
{
    private readonly WebSocketHttpListenerTransportOptions _options;
    private readonly PulseDispatcher _dispatcher;
    private readonly IPulseLogger _logger;
    private readonly WebSocketHttpListenerTransport _transport;

    private readonly SemaphoreSlim? _concurrencySemaphore;
    private readonly ConcurrentDictionary<string, Task> _activeHandlers = new();

    public WebSocketHttpListenerTransportHostedService(
        IOptions<WebSocketHttpListenerTransportOptions> options,
        PulseDispatcher dispatcher,
        IPulseLogger logger)
    {
        _options = options.Value;
        _options.Validate();
        _dispatcher = dispatcher;
        _logger = logger;
        _transport = new WebSocketHttpListenerTransport(_options, _logger);

        if (_options.MaxConcurrentConnections is int maxConcurrentConnections)
            _concurrencySemaphore = new SemaphoreSlim(maxConcurrentConnections, maxConcurrentConnections);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (IPulseTransportConnection transportConnection in
            _transport.AcceptConnectionsAsync(stoppingToken))
        {
            string connectionId = transportConnection.ConnectionId;

            Task handlerTask = Task.Run(
                () => HandleConnectionAsync(transportConnection, stoppingToken),
                CancellationToken.None);

            _activeHandlers[connectionId] = handlerTask;
            _ = handlerTask.ContinueWith(
                _ => _activeHandlers.TryRemove(connectionId, out Task? _),
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }
    }

    private async Task HandleConnectionAsync(
        IPulseTransportConnection transportConnection,
        CancellationToken cancellationToken)
    {
        bool concurrencySemaphoreAcquired = false;

        try
        {
            if (_concurrencySemaphore != null)
            {
                concurrencySemaphoreAcquired = await _concurrencySemaphore.WaitAsync(0, cancellationToken);
                if (!concurrencySemaphoreAcquired)
                {
                    _logger.LogWarning(
                        $"[{transportConnection.ConnectionId}] Rejected: concurrency limit reached");
                    await transportConnection.CloseAsync(1008, "Server at capacity", CancellationToken.None);
                    await transportConnection.DisposeAsync();
                    return;
                }
            }

            await PulseConnectionLifecycle.HandleAsync(
                transportConnection,
                _dispatcher,
                _logger,
                cancellationToken);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception unhandledException)
        {
            _logger.LogError(
                $"[{transportConnection.ConnectionId}] Unhandled connection exception",
                unhandledException);
        }
        finally
        {
            if (concurrencySemaphoreAcquired)
                _concurrencySemaphore?.Release();
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInfo("WebSocket HttpListener transport stopping...");

        await _transport.DisposeAsync();

        foreach (string connectionId in _activeHandlers.Keys.ToArray())
        {
            try
            {
                await _dispatcher.ConnectionManager.DisconnectAsync(
                    connectionId,
                    closeCode: 1001,
                    reason: "Server shutting down",
                    cancellationToken: cancellationToken);
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

        await base.StopAsync(cancellationToken);
    }

    public override void Dispose()
    {
        _concurrencySemaphore?.Dispose();
        base.Dispose();
        GC.SuppressFinalize(this);
    }
}
