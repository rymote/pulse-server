namespace Rymote.Pulse.Attributes;

[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public class PulseEventAttribute : Attribute
{
    public string? Handle { get; }

    public PulseEventAttribute(string? handle = null)
    {
        Handle = handle;
    }
}