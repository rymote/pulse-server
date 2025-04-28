using System;

namespace Rymote.Pulse.Core.Exceptions;

public class PulseException : Exception
{
    public PulseStatus Status { get; }
    
    public PulseException(PulseStatus status, string message) : base(message)
    {
        Status = status;
    }
    
    public PulseException(PulseStatus status, string message, Exception innerException) : base(message, innerException)
    {
        Status = status;
    }
}