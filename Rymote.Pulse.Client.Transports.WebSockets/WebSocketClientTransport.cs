using System.Net.WebSockets;
using Rymote.Pulse.Core.Logging;
using Rymote.Pulse.Core.Transport;
using Rymote.Pulse.Transports.Multiplexing;

namespace Rymote.Pulse.Client.Transports.WebSockets;

public sealed class WebSocketClientTransport : IPulseClientTransport
{
    public string Name => "websocket";

    private readonly WebSocketClientTransportOptions _options;
    private readonly IPulseLogger _logger;

    public WebSocketClientTransport(
        WebSocketClientTransportOptions? options = null,
        IPulseLogger? logger = null)
    {
        _options = options ?? new WebSocketClientTransportOptions();
        _logger = logger ?? new PulseConsoleLogger(enableDebugLogs: false);
    }

    public async Task<IPulseSession> ConnectAsync(
        Uri endpoint,
        string? authToken,
        IReadOnlyDictionary<string, string>? queryParameters,
        CancellationToken cancellationToken)
    {
        Uri webSocketUri = BuildWebSocketUri(endpoint, queryParameters);

        ClientWebSocket clientWebSocket = new ClientWebSocket();
        if (!string.IsNullOrEmpty(authToken))
            clientWebSocket.Options.SetRequestHeader("Authorization", $"Bearer {authToken}");

        await clientWebSocket.ConnectAsync(webSocketUri, cancellationToken).ConfigureAwait(false);

        WebSocketByteStream byteStream = new WebSocketByteStream(
            clientWebSocket, _options.InitialReceiveBufferSizeInBytes);

        PulseStreamMultiplexerOptions multiplexerOptions = new PulseStreamMultiplexerOptions
        {
            MaxFramePayloadSizeInBytes = _options.MaxFramePayloadSizeInBytes,
            MaxDatagramEnvelopeSizeInBytes = _options.MaxDatagramEnvelopeSizeInBytes,
            DatagramsEnabled = _options.DatagramsEnabled
        };

        Dictionary<string, object> initialMetadata = new Dictionary<string, object>
        {
            ["websocket"] = clientWebSocket
        };

        PulseStreamMultiplexer multiplexer = new PulseStreamMultiplexer(
            byteStream,
            isServerSide: false,
            transportName: Name,
            logger: _logger,
            options: multiplexerOptions,
            queryParameters: queryParameters?.ToDictionary(keyValuePair => keyValuePair.Key, keyValuePair => keyValuePair.Value),
            initialMetadata: initialMetadata);

        multiplexer.Start();
        return multiplexer;
    }

    private static Uri BuildWebSocketUri(Uri endpoint, IReadOnlyDictionary<string, string>? queryParameters)
    {
        UriBuilder builder = new UriBuilder(endpoint);

        if (builder.Scheme.Equals("http", StringComparison.OrdinalIgnoreCase))
            builder.Scheme = "ws";
        else if (builder.Scheme.Equals("https", StringComparison.OrdinalIgnoreCase))
            builder.Scheme = "wss";

        if (queryParameters != null && queryParameters.Count > 0)
        {
            string existingQuery = builder.Query.TrimStart('?');
            List<string> queryParts = new List<string>();
            if (!string.IsNullOrEmpty(existingQuery)) queryParts.Add(existingQuery);

            foreach (KeyValuePair<string, string> entry in queryParameters)
                queryParts.Add($"{Uri.EscapeDataString(entry.Key)}={Uri.EscapeDataString(entry.Value)}");

            builder.Query = string.Join('&', queryParts);
        }

        return builder.Uri;
    }
}
