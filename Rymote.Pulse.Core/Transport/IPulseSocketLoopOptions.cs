namespace Rymote.Pulse.Core.Transport;

public interface IPulseSocketLoopOptions
{
    int BufferSizeInBytes { get; }
    int MaxMessageSizeInBytes { get; }
}
