namespace Rymote.Pulse.Core;

public static class Versioning
{
    public static bool IsCompatible(string requestedVersion, string routeVersion)
    {
        return string.Equals(requestedVersion, routeVersion, StringComparison.OrdinalIgnoreCase);
    }
}