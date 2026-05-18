using System.Buffers.Binary;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using Rymote.Pulse.Core.Transport;

namespace Rymote.Pulse.Transports.RawTcp;

internal sealed class RawTcpTransportConnection : IPulseTransportConnection
{
    public string ConnectionId { get; }
    public string TransportName => "raw-tcp";
    public bool IsOpen => !_disposed && _tcpClient.Connected;
    public IReadOnlyDictionary<string, string> QueryParameters { get; } = new Dictionary<string, string>();
    public IReadOnlyDictionary<string, object> InitialMetadata { get; }

    private readonly TcpClient _tcpClient;
    private readonly Stream _stream;
    private readonly int _maxMessageSizeInBytes;
    private readonly SemaphoreSlim _sendSemaphore = new SemaphoreSlim(1, 1);
    private bool _disposed;

    public RawTcpTransportConnection(
        string connectionId,
        TcpClient tcpClient,
        Stream stream,
        X509Certificate2? peerCertificate,
        int maxMessageSizeInBytes)
    {
        ConnectionId = connectionId;
        _tcpClient = tcpClient;
        _stream = stream;
        _maxMessageSizeInBytes = maxMessageSizeInBytes;

        Dictionary<string, object> metadata = new Dictionary<string, object>();
        if (tcpClient.Client.RemoteEndPoint is IPEndPoint remoteEndPoint)
            metadata["remote_endpoint"] = remoteEndPoint;
        if (peerCertificate != null)
            metadata["peer_certificate"] = peerCertificate;
        InitialMetadata = metadata;
    }

    public async ValueTask<ReadOnlyMemory<byte>?> ReceiveMessageAsync(CancellationToken cancellationToken)
    {
        byte[] lengthPrefixBuffer = new byte[4];
        if (!await ReadExactlyAsync(lengthPrefixBuffer, cancellationToken).ConfigureAwait(false))
            return null;

        uint declaredLength = BinaryPrimitives.ReadUInt32BigEndian(lengthPrefixBuffer);
        if (declaredLength == 0 || declaredLength > _maxMessageSizeInBytes)
            return null;

        byte[] payload = new byte[(int)declaredLength];
        if (!await ReadExactlyAsync(payload, cancellationToken).ConfigureAwait(false))
            return null;

        return payload;
    }

    private async ValueTask<bool> ReadExactlyAsync(byte[] buffer, CancellationToken cancellationToken)
    {
        int bytesRead = 0;
        while (bytesRead < buffer.Length)
        {
            int chunk;
            try
            {
                chunk = await _stream.ReadAsync(
                    buffer.AsMemory(bytesRead, buffer.Length - bytesRead),
                    cancellationToken).ConfigureAwait(false);
            }
            catch (IOException)
            {
                return false;
            }
            catch (ObjectDisposedException)
            {
                return false;
            }
            catch (OperationCanceledException)
            {
                return false;
            }

            if (chunk == 0) return false;
            bytesRead += chunk;
        }
        return true;
    }

    public async ValueTask SendMessageAsync(ReadOnlyMemory<byte> payload, CancellationToken cancellationToken)
    {
        if (payload.Length > _maxMessageSizeInBytes)
            throw new InvalidOperationException(
                $"Payload size {payload.Length} exceeds MaxMessageSizeInBytes ({_maxMessageSizeInBytes}).");

        await _sendSemaphore.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            byte[] lengthPrefix = new byte[4];
            BinaryPrimitives.WriteUInt32BigEndian(lengthPrefix, (uint)payload.Length);

            await _stream.WriteAsync(lengthPrefix.AsMemory(), cancellationToken).ConfigureAwait(false);
            await _stream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
            await _stream.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _sendSemaphore.Release();
        }
    }

    public ValueTask CloseAsync(int closeCode, string? reason, CancellationToken cancellationToken)
    {
        if (_disposed) return ValueTask.CompletedTask;

        try
        {
            _tcpClient.Close();
        }
        catch
        {
            // ignore — best-effort close
        }

        return ValueTask.CompletedTask;
    }

    public async ValueTask DisposeAsync()
    {
        if (_disposed) return;
        _disposed = true;

        try { await _stream.DisposeAsync().ConfigureAwait(false); } catch { /* ignore */ }
        try { _tcpClient.Dispose(); } catch { /* ignore */ }
        _sendSemaphore.Dispose();
        GC.SuppressFinalize(this);
    }
}
