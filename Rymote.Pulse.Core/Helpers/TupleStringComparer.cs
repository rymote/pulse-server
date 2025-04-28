using System;
using System.Collections.Generic;

namespace Rymote.Pulse.Core.Helpers;

public class TupleStringComparer : IEqualityComparer<(string Route, string Version)>
{
    public bool Equals((string Route, string Version) x, (string Route, string Version) y)
    {
        return StringComparer.OrdinalIgnoreCase.Equals(x.Route, y.Route) &&
               StringComparer.OrdinalIgnoreCase.Equals(x.Version, y.Version);
    }

    public int GetHashCode((string Route, string Version) obj)
    {
        unchecked
        {
            int hash = 17;
            hash = hash * 23 + StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Route);
            hash = hash * 23 + StringComparer.OrdinalIgnoreCase.GetHashCode(obj.Version);
            return hash;
        }
    }
}