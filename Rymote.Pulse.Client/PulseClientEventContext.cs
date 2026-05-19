using Rymote.Pulse.Core.Logging;
using Rymote.Pulse.Core.Messages;

namespace Rymote.Pulse.Client;

public class PulseClientEventContext
{
    public string Handle { get; }
    public Dictionary<string, string> Parameters { get; }
    public PulseEnvelope<object> UntypedEnvelope { get; }
    public byte[] RawEnvelopeBytes { get; }
    public string TransportName { get; }
    public IPulseLogger Logger { get; }

    public PulseClientEventContext(
        string handle,
        Dictionary<string, string> parameters,
        PulseEnvelope<object> untypedEnvelope,
        byte[] rawEnvelopeBytes,
        string transportName,
        IPulseLogger logger)
    {
        Handle = handle;
        Parameters = parameters;
        UntypedEnvelope = untypedEnvelope;
        RawEnvelopeBytes = rawEnvelopeBytes;
        TransportName = transportName;
        Logger = logger;
    }
}
