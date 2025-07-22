namespace Rymote.Pulse.Attributes;

[AttributeUsage(AttributeTargets.Method, AllowMultiple = false)]
public class PulseRpcAttribute : Attribute
{
    public string? Handle { get; }

    public PulseRpcAttribute(string? handle = null)
    {
        Handle = handle;
    }
}