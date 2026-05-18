using System.Net;
using System.Net.Security;
using System.Security.Cryptography.X509Certificates;

namespace Rymote.Pulse.Transports.RawTcp;

public class RawTcpTransportOptions
{
    public IPEndPoint Endpoint { get; set; } = new IPEndPoint(IPAddress.Any, 9000);
    public int MaxMessageSizeInBytes { get; set; } = 10 * 1024 * 1024;
    public int? MaxConcurrentConnections { get; set; }
    public X509Certificate2? ServerCertificate { get; set; }
    public RemoteCertificateValidationCallback? ClientCertificateValidationCallback { get; set; }
    public TimeSpan ShutdownDrainTimeout { get; set; } = TimeSpan.FromSeconds(10);

    internal void Validate()
    {
        if (MaxMessageSizeInBytes <= 0)
            throw new InvalidOperationException(
                $"{nameof(RawTcpTransportOptions)}.{nameof(MaxMessageSizeInBytes)} must be greater than zero.");

        if (MaxConcurrentConnections is <= 0)
            throw new InvalidOperationException(
                $"{nameof(RawTcpTransportOptions)}.{nameof(MaxConcurrentConnections)} must be greater than zero when set.");

        if (ShutdownDrainTimeout < TimeSpan.Zero)
            throw new InvalidOperationException(
                $"{nameof(RawTcpTransportOptions)}.{nameof(ShutdownDrainTimeout)} cannot be negative.");
    }
}
