namespace Rymote.Pulse.Attributes;

[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public class PulseStreamAttribute : Attribute
{
    public string? Handle { get; }

    public PulseStreamAttribute(string? handle = null)
    {
        Handle = handle;
    }
}