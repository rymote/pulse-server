namespace Rymote.Pulse.Client.Transports.WebTransport;

public class WebTransportClientTransportOptions
{
    public int MaxStreamEnvelopeSizeInBytes { get; set; } = 10 * 1024 * 1024;
    public int MaxDatagramEnvelopeSizeInBytes { get; set; } = 1200;
    public bool DatagramsEnabled { get; set; } = true;

    /// <summary>
    /// SHA-256 fingerprints of server certificates the client should accept.
    /// Mirrors the browser <c>WebTransport</c> <c>serverCertificateHashes</c> option —
    /// useful in development against short-lived ECDSA P-256 certs.
    /// </summary>
    public byte[][]? ServerCertificateHashes { get; set; }
}
