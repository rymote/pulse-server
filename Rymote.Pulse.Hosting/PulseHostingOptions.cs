using System.Net;
using Rymote.Pulse.Core.Transport;

namespace Rymote.Pulse.Hosting;

public class PulseHostingOptions : IPulseSocketLoopOptions
{
    public IList<string> Prefixes { get; } = new List<string>();
    public int BufferSizeInBytes { get; set; } = 4 * 1024;
    public int MaxMessageSizeInBytes { get; set; } = 10 * 1024 * 1024;
    public int? MaxConcurrentConnections { get; set; }
    public AuthenticationSchemes AuthenticationSchemes { get; set; } = AuthenticationSchemes.Anonymous;
    public TimeSpan ShutdownDrainTimeout { get; set; } = TimeSpan.FromSeconds(10);

    internal void Validate()
    {
        if (Prefixes.Count == 0)
            throw new InvalidOperationException(
                $"{nameof(PulseHostingOptions)}.{nameof(Prefixes)} must contain at least one HTTP prefix (e.g. \"http://+:8080/pulse/\").");

        if (BufferSizeInBytes <= 0)
            throw new InvalidOperationException(
                $"{nameof(PulseHostingOptions)}.{nameof(BufferSizeInBytes)} must be greater than zero.");

        if (MaxMessageSizeInBytes < BufferSizeInBytes)
            throw new InvalidOperationException(
                $"{nameof(PulseHostingOptions)}.{nameof(MaxMessageSizeInBytes)} must be greater than or equal to {nameof(BufferSizeInBytes)}.");

        if (MaxConcurrentConnections is <= 0)
            throw new InvalidOperationException(
                $"{nameof(PulseHostingOptions)}.{nameof(MaxConcurrentConnections)} must be greater than zero when set.");

        if (ShutdownDrainTimeout < TimeSpan.Zero)
            throw new InvalidOperationException(
                $"{nameof(PulseHostingOptions)}.{nameof(ShutdownDrainTimeout)} cannot be negative.");
    }
}
