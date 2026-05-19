using System.Net;
using System.Net.Quic;
using System.Net.Security;
using System.Security.Authentication;
using System.Text;
using Rymote.Pulse.Client.Transports.WebTransport.Internal;
using Rymote.Pulse.Core.Logging;
using Rymote.Pulse.Core.Transport;

namespace Rymote.Pulse.Client.Transports.WebTransport;

/// <summary>
/// WebTransport client transport for Rymote.Pulse.
///
/// <para>
/// Builds the HTTP/3 + WebTransport CONNECT handshake on top of <see cref="QuicConnection"/>:
/// </para>
/// <list type="number">
///   <item>Opens a QUIC connection with ALPN <c>"h3"</c>.</item>
///   <item>Opens a unidirectional control stream and sends a minimal SETTINGS frame
///         (including <c>SETTINGS_ENABLE_WEBTRANSPORT</c> and <c>SETTINGS_H3_DATAGRAM</c>).</item>
///   <item>Opens a bidirectional stream for the extended CONNECT request encoded with QPACK
///         (static-table + literal fields; no dynamic table, no Huffman).</item>
///   <item>Reads the response and confirms a <c>:status = 200</c> indexed-field byte is present
///         (heuristic — Kestrel emits the static-indexed form).</item>
///   <item>Returns a <see cref="WebTransportClientPulseSession"/> wrapping the QUIC connection.
///         Subsequent WT streams are framed with the WebTransport stream prefix per the draft spec.</item>
/// </list>
///
/// <para>
/// <b>Known limitations vs. a fully spec-compliant HTTP/3 client:</b>
/// </para>
/// <list type="bullet">
///   <item>No QPACK dynamic table.</item>
///   <item>No Huffman coding on names/values.</item>
///   <item>Response-header decoding is heuristic (single-byte indexed scan).</item>
///   <item>WebTransport datagrams are not surfaced (matches the server-side limitation).</item>
///   <item>Control stream is one-way (no server SETTINGS consumption).</item>
/// </list>
/// </summary>
public sealed class WebTransportClientTransport : IPulseClientTransport
{
    public string Name => "webtransport";

    private readonly WebTransportClientTransportOptions _options;
    private readonly IPulseLogger _logger;

    public WebTransportClientTransport(
        WebTransportClientTransportOptions? options = null,
        IPulseLogger? logger = null)
    {
        _options = options ?? new WebTransportClientTransportOptions();
        _logger = logger ?? new PulseConsoleLogger(enableDebugLogs: false);
    }

    public async Task<IPulseSession> ConnectAsync(
        Uri endpoint,
        string? authToken,
        IReadOnlyDictionary<string, string>? queryParameters,
        CancellationToken cancellationToken)
    {
        if (!QuicConnection.IsSupported)
            throw new PlatformNotSupportedException(
                "System.Net.Quic is not supported on this platform (MsQuic missing).");

        IPAddress[] resolvedAddresses = await Dns.GetHostAddressesAsync(endpoint.Host, cancellationToken)
            .ConfigureAwait(false);
        if (resolvedAddresses.Length == 0)
            throw new InvalidOperationException($"Could not resolve host '{endpoint.Host}'.");

        QuicClientConnectionOptions connectionOptions = new QuicClientConnectionOptions
        {
            RemoteEndPoint = new IPEndPoint(resolvedAddresses[0], endpoint.Port == -1 ? 443 : endpoint.Port),
            DefaultStreamErrorCode = 0,
            DefaultCloseErrorCode = 0,
            ClientAuthenticationOptions = new SslClientAuthenticationOptions
            {
                ApplicationProtocols = new List<SslApplicationProtocol>
                {
                    new SslApplicationProtocol(Http3Constants.ALPN_H3)
                },
                TargetHost = endpoint.Host,
                EnabledSslProtocols = SslProtocols.Tls13,
                RemoteCertificateValidationCallback = BuildCertificateValidator()
            }
        };

        QuicConnection quicConnection =
            await QuicConnection.ConnectAsync(connectionOptions, cancellationToken).ConfigureAwait(false);

        try
        {
            await SendControlStreamAndSettingsAsync(quicConnection, cancellationToken).ConfigureAwait(false);

            IReadOnlyDictionary<ulong, ulong> serverSettings =
                await Http3ControlStreamReader.AcceptAndReadSettingsAsync(quicConnection, _logger, cancellationToken)
                    .ConfigureAwait(false);

            if (!serverSettings.TryGetValue(Http3Constants.SETTING_ENABLE_WEBTRANSPORT, out ulong webTransportEnabled)
                || webTransportEnabled != 1)
            {
                throw new InvalidOperationException(
                    "WebTransport handshake: server SETTINGS did not advertise SETTINGS_ENABLE_WEBTRANSPORT = 1. " +
                    "The server is not WebTransport-capable.");
            }

            QuicStream connectStream = await PerformExtendedConnectAsync(
                quicConnection, endpoint, authToken, queryParameters, cancellationToken).ConfigureAwait(false);

            Dictionary<string, object> initialMetadata = new Dictionary<string, object>
            {
                ["quic_connection"] = quicConnection,
                ["remote_endpoint"] = quicConnection.RemoteEndPoint!
            };

            return new WebTransportClientPulseSession(
                quicConnection,
                (ulong)connectStream.Id,
                _options,
                queryParameters?.ToDictionary(keyValuePair => keyValuePair.Key, keyValuePair => keyValuePair.Value),
                initialMetadata);
        }
        catch
        {
            try { await quicConnection.DisposeAsync().ConfigureAwait(false); }
            catch { /* ignore */ }
            throw;
        }
    }

    private RemoteCertificateValidationCallback BuildCertificateValidator()
    {
        if (_options.ServerCertificateHashes != null && _options.ServerCertificateHashes.Length > 0)
        {
            byte[][] expectedHashes = _options.ServerCertificateHashes;
            return (_, presentedCertificate, _, _) =>
            {
                if (presentedCertificate is null) return false;
                using System.Security.Cryptography.SHA256 sha = System.Security.Cryptography.SHA256.Create();
                byte[] actualHash = sha.ComputeHash(presentedCertificate.GetRawCertData());
                foreach (byte[] expected in expectedHashes)
                {
                    if (expected.Length == actualHash.Length && expected.AsSpan().SequenceEqual(actualHash))
                        return true;
                }
                return false;
            };
        }

        return (_, _, _, errors) => errors == SslPolicyErrors.None;
    }

    private static async Task SendControlStreamAndSettingsAsync(
        QuicConnection quicConnection,
        CancellationToken cancellationToken)
    {
        QuicStream controlStream =
            await quicConnection.OpenOutboundStreamAsync(QuicStreamType.Unidirectional, cancellationToken)
                .ConfigureAwait(false);

        // Stream prefix: varint control-stream-type (0x00).
        byte[] streamTypePrefix = new byte[8];
        int writtenLength = Http3VarInt.Encode(Http3Constants.STREAM_CONTROL, streamTypePrefix);
        await controlStream.WriteAsync(streamTypePrefix.AsMemory(0, writtenLength), cancellationToken)
            .ConfigureAwait(false);

        // SETTINGS frame body — opt into WebTransport + HTTP/3 datagrams.
        byte[] settingsPayload = Http3FrameWriter.BuildSettingsPayload(new[]
        {
            (Http3Constants.SETTING_ENABLE_WEBTRANSPORT, 1UL),
            (Http3Constants.SETTING_H3_DATAGRAM, 1UL),
        });

        await Http3FrameWriter.WriteFrameAsync(
            controlStream, Http3Constants.FRAME_SETTINGS, settingsPayload, cancellationToken)
            .ConfigureAwait(false);
        await controlStream.FlushAsync(cancellationToken).ConfigureAwait(false);

        // Control stream stays open for the connection lifetime.
    }

    private async Task<QuicStream> PerformExtendedConnectAsync(
        QuicConnection quicConnection,
        Uri endpoint,
        string? authToken,
        IReadOnlyDictionary<string, string>? queryParameters,
        CancellationToken cancellationToken)
    {
        QuicStream connectStream =
            await quicConnection.OpenOutboundStreamAsync(QuicStreamType.Bidirectional, cancellationToken)
                .ConfigureAwait(false);

        List<(string Name, string Value)> headers = new List<(string, string)>
        {
            (":method", "CONNECT"),
            (":protocol", "webtransport"),
            (":scheme", "https"),
            (":authority", endpoint.Authority),
            (":path", BuildPathWithQuery(endpoint, queryParameters)),
            ("sec-webtransport-http3-draft", "1"),
            ("user-agent", "Rymote.Pulse.Client/3.0")
        };

        if (!string.IsNullOrEmpty(authToken))
            headers.Add(("authorization", $"Bearer {authToken}"));

        QpackHeaderEncoder encoder = new QpackHeaderEncoder();
        byte[] headerBlock = encoder.BuildHeaderBlock(headers);

        await Http3FrameWriter.WriteFrameAsync(
            connectStream, Http3Constants.FRAME_HEADERS, headerBlock, cancellationToken)
            .ConfigureAwait(false);
        await connectStream.FlushAsync(cancellationToken).ConfigureAwait(false);

        await VerifyConnectResponseAsync(connectStream, cancellationToken).ConfigureAwait(false);

        return connectStream;
    }

    private static string BuildPathWithQuery(Uri endpoint, IReadOnlyDictionary<string, string>? queryParameters)
    {
        string basePath = string.IsNullOrEmpty(endpoint.AbsolutePath) ? "/" : endpoint.AbsolutePath;
        if (queryParameters == null || queryParameters.Count == 0)
            return basePath;

        StringBuilder pathBuilder = new StringBuilder(basePath);
        pathBuilder.Append(endpoint.Query.Length > 0 ? '&' : '?');
        bool firstParameter = true;
        foreach (KeyValuePair<string, string> entry in queryParameters)
        {
            if (!firstParameter) pathBuilder.Append('&');
            pathBuilder.Append(Uri.EscapeDataString(entry.Key));
            pathBuilder.Append('=');
            pathBuilder.Append(Uri.EscapeDataString(entry.Value));
            firstParameter = false;
        }
        return pathBuilder.ToString();
    }

    private static async Task VerifyConnectResponseAsync(
        QuicStream connectStream,
        CancellationToken cancellationToken)
    {
        // Read HEADERS frame: [varint type][varint length][payload bytes].
        ulong frameType = await Http3VarInt.ReadAsync(connectStream, cancellationToken).ConfigureAwait(false);
        if (frameType != Http3Constants.FRAME_HEADERS)
            throw new InvalidOperationException(
                $"WebTransport handshake: expected HEADERS frame (0x{Http3Constants.FRAME_HEADERS:X}), got 0x{frameType:X}.");

        ulong frameLength = await Http3VarInt.ReadAsync(connectStream, cancellationToken).ConfigureAwait(false);
        if (frameLength == 0 || frameLength > 16 * 1024)
            throw new InvalidOperationException(
                $"WebTransport handshake: unexpected HEADERS payload length {frameLength}.");

        byte[] headerBlock = new byte[(int)frameLength];
        int bytesRead = 0;
        while (bytesRead < headerBlock.Length)
        {
            int chunk = await connectStream.ReadAsync(
                headerBlock.AsMemory(bytesRead, headerBlock.Length - bytesRead), cancellationToken)
                .ConfigureAwait(false);
            if (chunk == 0) throw new EndOfStreamException("WebTransport handshake: response truncated.");
            bytesRead += chunk;
        }

        IReadOnlyList<(string Name, string Value)> decodedHeaders = QpackHeaderDecoder.Decode(headerBlock);

        string? statusValue = null;
        foreach ((string headerName, string headerValue) in decodedHeaders)
        {
            if (string.Equals(headerName, ":status", StringComparison.Ordinal))
            {
                statusValue = headerValue;
                break;
            }
        }

        if (statusValue == null)
            throw new InvalidOperationException(
                "WebTransport handshake: response HEADERS did not contain a `:status` field. " +
                "Server response may be malformed or use Huffman-encoded QPACK literals (not supported by this decoder).");

        if (statusValue != "200")
            throw new InvalidOperationException(
                $"WebTransport handshake: server rejected extended CONNECT with :status = {statusValue}.");
    }
}
