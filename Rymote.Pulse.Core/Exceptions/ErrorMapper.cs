using System;
using System.Threading.Tasks;

namespace Rymote.Pulse.Core.Exceptions;

public class ErrorMapper
{
    public static (PulseStatus, string) MapException(Exception exception)
    {
        return exception switch
        {
            PulseException pulseException => (pulseException.Status, pulseException.Message),
            TaskCanceledException or OperationCanceledException => (PulseStatus.TIMEOUT, "Operation timed out."),
            _ => (PulseStatus.INTERNAL_ERROR, exception.Message)
        };
    }
}