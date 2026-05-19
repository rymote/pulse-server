using System.Net;
using System.Net.WebSockets;
using System.Runtime.CompilerServices;
using Rymote.Pulse.Core.Logging;
using Rymote.Pulse.Core.Transport;
using Rymote.Pulse.Transports.Multiplexing;
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

    public async IAsyncEnumerable<IPulseSession> AcceptSessionsAsync(
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
            catch (HttpListenerException) { yield break; }
            catch (ObjectDisposedException) { yield break; }
            catch (InvalidOperationException) { yield break; }

            if (!httpListenerContext.Request.IsWebSocketRequest)
            {
                _logger.LogWarning(
                    $"Rejected non-WebSocket request: {httpListenerContext.Request.RawUrl}");
                httpListenerContext.Response.StatusCode = (int)HttpStatusCode.BadRequest;
                httpListenerContext.Response.Close();
                continue;
            }

            IPulseSession? session = await TryEstablishSessionAsync(httpListenerContext).ConfigureAwait(false);
            if (session == null) continue;

            yield return session;
        }
    }

    private async Task<IPulseSession?> TryEstablishSessionAsync(HttpListenerContext httpListenerContext)
    {
        HttpListenerWebSocketContext? webSocketContext = null;
        try
        {
            webSocketContext = await httpListenerContext.AcceptWebSocketAsync(subProtocol: null).ConfigureAwait(false);
        }
        catch (Exception acceptException)
        {
            _logger.LogError($"Error accepting WebSocket: {acceptException.Message}", acceptException);
        }

        if (webSocketContext == null) return null;

        WebSocketByteStream byteStream = new WebSocketByteStream(
            webSocketContext.WebSocket, _options.InitialReceiveBufferSizeInBytes);

        Dictionary<string, string> queryParameters = new Dictionary<string, string>();
        foreach (string? key in httpListenerContext.Request.QueryString.AllKeys)
        {
            if (key == null) continue;
            queryParameters[key] = httpListenerContext.Request.QueryString[key] ?? string.Empty;
        }

        Dictionary<string, object> initialMetadata = new Dictionary<string, object>
        {
            ["websocket"] = webSocketContext.WebSocket,
            ["http_listener_context"] = httpListenerContext,
            ["ip_address"] = ResolveClientIpAddress(httpListenerContext),
            ["user_agent"] = httpListenerContext.Request.UserAgent ?? "Unknown",
            ["origin"] = httpListenerContext.Request.Headers["Origin"] ?? "Unknown"
        };

        if (httpListenerContext.User != null)
            initialMetadata["user_principal"] = httpListenerContext.User;

        PulseStreamMultiplexerOptions multiplexerOptions = new PulseStreamMultiplexerOptions
        {
            MaxFramePayloadSizeInBytes = _options.MaxFramePayloadSizeInBytes,
            MaxDatagramEnvelopeSizeInBytes = _options.MaxDatagramEnvelopeSizeInBytes,
            DatagramsEnabled = _options.DatagramsEnabled
        };

        PulseStreamMultiplexer multiplexer = new PulseStreamMultiplexer(
            byteStream,
            isServerSide: true,
            transportName: Name,
            logger: _logger,
            options: multiplexerOptions,
            queryParameters: queryParameters,
            initialMetadata: initialMetadata);

        multiplexer.Start();
        return multiplexer;
    }

    private static string ResolveClientIpAddress(HttpListenerContext httpListenerContext)
    {
        string? forwardedFor = httpListenerContext.Request.Headers["X-Forwarded-For"];
        if (!string.IsNullOrEmpty(forwardedFor))
            return forwardedFor.Split(',')[0].Trim();

        string? realIp = httpListenerContext.Request.Headers["X-Real-IP"];
        if (!string.IsNullOrEmpty(realIp))
            return realIp;

        return httpListenerContext.Request.RemoteEndPoint?.Address.ToString() ?? "Unknown";
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
