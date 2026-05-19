using System.Collections.Concurrent;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using Rymote.Pulse.Core;
using Rymote.Pulse.Core.Logging;
using Rymote.Pulse.Core.Transport;

namespace Rymote.Pulse.Transports.RawTcp;

internal sealed class RawTcpTransportHostedService : BackgroundService
{
    private readonly RawTcpTransportOptions _options;
    private readonly PulseDispatcher _dispatcher;
    private readonly IPulseLogger _logger;
    private readonly RawTcpTransport _transport;

    private readonly SemaphoreSlim? _concurrencySemaphore;
    private readonly ConcurrentDictionary<string, Task> _activeHandlers = new();

    public RawTcpTransportHostedService(
        IOptions<RawTcpTransportOptions> options,
        PulseDispatcher dispatcher,
        IPulseLogger logger)
    {
        _options = options.Value;
        _options.Validate();
        _dispatcher = dispatcher;
        _logger = logger;
        _transport = new RawTcpTransport(_options, _logger);

        if (_options.MaxConcurrentConnections is int maxConcurrentConnections)
            _concurrencySemaphore = new SemaphoreSlim(maxConcurrentConnections, maxConcurrentConnections);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        await foreach (IPulseSession session in _transport.AcceptSessionsAsync(stoppingToken))
        {
            string sessionId = session.SessionId;

            Task handlerTask = Task.Run(
                () => HandleSessionAsync(session, stoppingToken),
                CancellationToken.None);

            _activeHandlers[sessionId] = handlerTask;
            _ = handlerTask.ContinueWith(
                _ => _activeHandlers.TryRemove(sessionId, out Task? _),
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }
    }

    private async Task HandleSessionAsync(IPulseSession session, CancellationToken cancellationToken)
    {
        bool concurrencySemaphoreAcquired = false;

        try
        {
            if (_concurrencySemaphore != null)
            {
                concurrencySemaphoreAcquired = await _concurrencySemaphore.WaitAsync(0, cancellationToken);
                if (!concurrencySemaphoreAcquired)
                {
                    _logger.LogWarning($"[{session.SessionId}] Rejected: concurrency limit reached");
                    await session.CloseAsync(reasonCode: 5, drainTimeout: TimeSpan.Zero, CancellationToken.None);
                    await session.DisposeAsync();
                    return;
                }
            }

            await PulseSessionLifecycle.HandleAsync(session, _dispatcher, _logger, cancellationToken);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception unhandledException)
        {
            _logger.LogError($"[{session.SessionId}] Unhandled session exception", unhandledException);
        }
        finally
        {
            if (concurrencySemaphoreAcquired)
                _concurrencySemaphore?.Release();
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        _logger.LogInfo("Raw TCP transport stopping...");

        await _transport.DisposeAsync();

        foreach (string sessionId in _activeHandlers.Keys.ToArray())
        {
            try
            {
                await _dispatcher.ConnectionManager.DisconnectAsync(
                    sessionId, reasonCode: 1001, reason: "Server shutting down", cancellationToken);
            }
            catch (Exception disconnectException)
            {
                _logger.LogDebug(
                    $"Error disconnecting {sessionId} during shutdown: {disconnectException.Message}");
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
                $"{_activeHandlers.Count} raw TCP session handler(s) did not drain within {_options.ShutdownDrainTimeout}.");
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
