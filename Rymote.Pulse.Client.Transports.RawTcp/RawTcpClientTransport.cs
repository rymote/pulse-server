using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using Rymote.Pulse.Core.Logging;
using Rymote.Pulse.Core.Transport;
using Rymote.Pulse.Transports.Multiplexing;

namespace Rymote.Pulse.Client.Transports.RawTcp;

public sealed class RawTcpClientTransport : IPulseClientTransport
{
    public string Name => "raw-tcp";

    private readonly RawTcpClientTransportOptions _options;
    private readonly IPulseLogger _logger;

    public RawTcpClientTransport(
        RawTcpClientTransportOptions options,
        IPulseLogger? logger = null)
    {
        _options = options;
        _logger = logger ?? new PulseConsoleLogger(enableDebugLogs: false);
    }

    public async Task<IPulseSession> ConnectAsync(
        Uri endpoint,
        string? authToken,
        IReadOnlyDictionary<string, string>? queryParameters,
        CancellationToken cancellationToken)
    {
        TcpClient tcpClient = new TcpClient();
        await tcpClient.ConnectAsync(_options.Endpoint, cancellationToken).ConfigureAwait(false);

        Stream byteStream;

        if (_options.UseTls)
        {
            SslStream sslStream = new SslStream(
                tcpClient.GetStream(),
                leaveInnerStreamOpen: false,
                userCertificateValidationCallback: _options.ServerCertificateValidationCallback);

            SslClientAuthenticationOptions sslAuthenticationOptions = new SslClientAuthenticationOptions
            {
                TargetHost = _options.TargetHost ?? _options.Endpoint.Address.ToString(),
                EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13,
                ClientCertificates = _options.ClientCertificates
            };

            try
            {
                await sslStream.AuthenticateAsClientAsync(sslAuthenticationOptions, cancellationToken)
                    .ConfigureAwait(false);
            }
            catch
            {
                await sslStream.DisposeAsync().ConfigureAwait(false);
                tcpClient.Dispose();
                throw;
            }

            byteStream = sslStream;
        }
        else
        {
            byteStream = tcpClient.GetStream();
        }

        PulseStreamMultiplexerOptions multiplexerOptions = new PulseStreamMultiplexerOptions
        {
            MaxFramePayloadSizeInBytes = _options.MaxFramePayloadSizeInBytes,
            MaxDatagramEnvelopeSizeInBytes = _options.MaxDatagramEnvelopeSizeInBytes,
            DatagramsEnabled = _options.DatagramsEnabled
        };

        PulseStreamMultiplexer multiplexer = new PulseStreamMultiplexer(
            byteStream,
            isServerSide: false,
            transportName: Name,
            logger: _logger,
            options: multiplexerOptions,
            queryParameters: queryParameters?.ToDictionary(keyValuePair => keyValuePair.Key, keyValuePair => keyValuePair.Value));

        multiplexer.Start();
        return multiplexer;
    }
}
