using Rymote.Pulse.Core;

namespace Rymote.Pulse.MediatR;

/// <summary>
/// Provides access to the current PulseContext within MediatR handlers
/// </summary>
public interface IPulseContextAccessor
{
    PulseContext? Context { get; set; }
}

/// <summary>
/// Implementation of IPulseContextAccessor
/// </summary>
public class PulseContextAccessor : IPulseContextAccessor
{
    public PulseContext? Context { get; set; }
} 