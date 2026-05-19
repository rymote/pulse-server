namespace Rymote.Pulse.Core.Transport;

public class PulseStreamResetException : Exception
{
    public int ReasonCode { get; }

    public PulseStreamResetException(int reasonCode)
        : base($"Pulse stream reset with reason code {reasonCode}.")
    {
        ReasonCode = reasonCode;
    }

    public PulseStreamResetException(int reasonCode, string message)
        : base(message)
    {
        ReasonCode = reasonCode;
    }
}
