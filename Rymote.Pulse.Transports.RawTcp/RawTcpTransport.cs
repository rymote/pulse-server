using System.Net.Security;
using System.Net.Sockets;
using System.Runtime.CompilerServices;
using System.Security.Authentication;
using System.Security.Cryptography.X509Certificates;
using Rymote.Pulse.Core.Logging;
using Rymote.Pulse.Core.Transport;

namespace Rymote.Pulse.Transports.RawTcp;

internal sealed class RawTcpTransport : IPulseTransport, IAsyncDisposable
{
    public string Name => "raw-tcp";

    private readonly RawTcpTransportOptions _options;
    private readonly IPulseLogger _logger;
    private TcpListener? _tcpListener;

    public RawTcpTransport(RawTcpTransportOptions options, IPulseLogger logger)
    {
        _options = options;
        _logger = logger;
    }

    public async IAsyncEnumerable<IPulseTransportConnection> AcceptConnectionsAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        _tcpListener = new TcpListener(_options.Endpoint);
        _tcpListener.Start();
        _logger.LogInfo($"Raw TCP transport listening on: {_tcpListener.LocalEndpoint}");

        await using CancellationTokenRegistration stopRegistration =
            cancellationToken.Register(StopListenerSafely);

        while (!cancellationToken.IsCancellationRequested)
        {
            TcpClient? tcpClient = await AcceptTcpClientSafelyAsync(cancellationToken).ConfigureAwait(false);
            if (tcpClient == null) yield break;

            IPulseTransportConnection? transportConnection =
                await TryEstablishConnectionAsync(tcpClient, cancellationToken).ConfigureAwait(false);
            if (transportConnection == null) continue;

            yield return transportConnection;
        }
    }

    private async Task<TcpClient?> AcceptTcpClientSafelyAsync(CancellationToken cancellationToken)
    {
        try
        {
            return await _tcpListener!.AcceptTcpClientAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { return null; }
        catch (ObjectDisposedException) { return null; }
        catch (SocketException) { return null; }
    }

    private async Task<IPulseTransportConnection?> TryEstablishConnectionAsync(
        TcpClient tcpClient,
        CancellationToken cancellationToken)
    {
        string connectionId = Guid.NewGuid().ToString();
        Stream stream;
        X509Certificate2? peerCertificate = null;

        if (_options.ServerCertificate != null)
        {
            SslStream sslStream = new SslStream(
                tcpClient.GetStream(),
                leaveInnerStreamOpen: false,
                userCertificateValidationCallback: _options.ClientCertificateValidationCallback);

            try
            {
                SslServerAuthenticationOptions sslAuthenticationOptions = new SslServerAuthenticationOptions
                {
                    ServerCertificate = _options.ServerCertificate,
                    ClientCertificateRequired = _options.ClientCertificateValidationCallback != null,
                    EnabledSslProtocols = SslProtocols.Tls12 | SslProtocols.Tls13
                };

                await sslStream.AuthenticateAsServerAsync(sslAuthenticationOptions, cancellationToken)
                    .ConfigureAwait(false);

                if (sslStream.RemoteCertificate is { } remoteCertificate)
                {
                    peerCertificate = remoteCertificate as X509Certificate2
                        ?? new X509Certificate2(remoteCertificate);
                }

                stream = sslStream;
            }
            catch (Exception authException)
            {
                _logger.LogError(
                    $"TLS handshake failed for {tcpClient.Client.RemoteEndPoint}: {authException.Message}",
                    authException);
                await sslStream.DisposeAsync().ConfigureAwait(false);
                tcpClient.Dispose();
                return null;
            }
        }
        else
        {
            stream = tcpClient.GetStream();
        }

        return new RawTcpTransportConnection(
            connectionId,
            tcpClient,
            stream,
            peerCertificate,
            _options.MaxMessageSizeInBytes);
    }

    private void StopListenerSafely()
    {
        try
        {
            _tcpListener?.Stop();
        }
        catch (Exception stopException)
        {
            _logger.LogDebug($"Error stopping TcpListener: {stopException.Message}");
        }
    }

    public ValueTask DisposeAsync()
    {
        try
        {
            _tcpListener?.Stop();
        }
        catch (Exception stopException)
        {
            _logger.LogDebug($"Error stopping TcpListener: {stopException.Message}");
        }
        return ValueTask.CompletedTask;
    }
}
