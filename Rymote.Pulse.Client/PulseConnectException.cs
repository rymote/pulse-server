namespace Rymote.Pulse.Client;

public class PulseConnectException : Exception
{
    public PulseConnectException(string message) : base(message) { }
    public PulseConnectException(string message, Exception? innerException) : base(message, innerException) { }
}
