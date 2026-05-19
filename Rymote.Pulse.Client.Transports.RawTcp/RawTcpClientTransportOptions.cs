using System.Net;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;

namespace Rymote.Pulse.Client.Transports.RawTcp;

public class RawTcpClientTransportOptions
{
    public required IPEndPoint Endpoint { get; set; }
    public int MaxFramePayloadSizeInBytes { get; set; } = 10 * 1024 * 1024;
    public int MaxDatagramEnvelopeSizeInBytes { get; set; } = 1200;
    public bool DatagramsEnabled { get; set; } = true;
    public bool UseTls { get; set; }
    public string? TargetHost { get; set; }
    public RemoteCertificateValidationCallback? ServerCertificateValidationCallback { get; set; }
    public X509Certificate2Collection? ClientCertificates { get; set; }
}
