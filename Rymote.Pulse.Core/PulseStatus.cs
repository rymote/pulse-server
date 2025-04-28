namespace Rymote.Pulse.Core;

public enum PulseStatus
{
    OK = 0,
    BAD_REQUEST = 1,
    UNAUTHORIZED = 2,
    NOT_FOUND = 3,
    INTERNAL_ERROR = 4,
    TIMEOUT = 5,
    UNSUPPORTED = 6,
    CONFLICT = 7
}