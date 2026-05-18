using System.Net;
using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using Rymote.Pulse.Core.Logging;
using Rymote.Pulse.Core.Transport;
using HttpListenerType = System.Net.HttpListener;

namespace Rymote.Pulse.Transports.WebSockets.HttpListener;

internal sealed class WebSocketHttpListenerTransport : IPulseTransport, IAsyncDisposable
{
    public string Name => "websocket-httplistener";

    private readonly WebSocketHttpListenerTransportOptions _options;
    private readonly IPulseLogger _logger;
    private HttpListenerType? _httpListener;

    public WebSocketHttpListenerTransport(WebSocketHttpListenerTransportOptions options, IPulseLogger logger)
    {
        _options = options;
        _logger = logger;
    }

    public async IAsyncEnumerable<IPulseTransportConnection> AcceptConnectionsAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        _httpListener = new HttpListenerType
        {
            AuthenticationSchemes = _options.AuthenticationSchemes
        };

        foreach (string prefix in _options.Prefixes)
            _httpListener.Prefixes.Add(prefix);

        _httpListener.Start();
        _logger.LogInfo(
            $"WebSocket HttpListener transport listening on: {string.Join(", ", _options.Prefixes)}");

        await using CancellationTokenRegistration stopRegistration =
            cancellationToken.Register(StopListenerSafely);

        while (!cancellationToken.IsCancellationRequested)
        {
            HttpListenerContext httpListenerContext;
            try
            {
                httpListenerContext = await _httpListener.GetContextAsync().ConfigureAwait(false);
            }
            catch (HttpListenerException)
            {
                yield break;
            }
            catch (ObjectDisposedException)
            {
                yield break;
            }
            catch (InvalidOperationException)
            {
                yield break;
            }

            if (!httpListenerContext.Request.IsWebSocketRequest)
            {
                _logger.LogWarning(
                    $"Rejected non-WebSocket request: {httpListenerContext.Request.RawUrl}");
                httpListenerContext.Response.StatusCode = (int)HttpStatusCode.BadRequest;
                httpListenerContext.Response.Close();
                continue;
            }

            HttpListenerWebSocketContext? webSocketContext = null;
            try
            {
                webSocketContext = await httpListenerContext.AcceptWebSocketAsync(subProtocol: null)
                    .ConfigureAwait(false);
            }
            catch (Exception acceptException)
            {
                _logger.LogError(
                    $"Error accepting WebSocket: {acceptException.Message}",
                    acceptException);
            }

            if (webSocketContext == null) continue;

            string connectionId = Guid.NewGuid().ToString();
            yield return new WebSocketHttpListenerConnection(
                connectionId,
                webSocketContext.WebSocket,
                httpListenerContext,
                _options.BufferSizeInBytes,
                _options.MaxMessageSizeInBytes);
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

    public ValueTask DisposeAsync()
    {
        try
        {
            _httpListener?.Close();
        }
        catch (Exception closeException)
        {
            _logger.LogDebug($"Error closing HttpListener: {closeException.Message}");
        }
        return ValueTask.CompletedTask;
    }
}
