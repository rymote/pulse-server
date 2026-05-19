namespace Rymote.Pulse.Client.Transports.WebTransport.Internal;

/// <summary>
/// QPACK static table (RFC 9204 Appendix A). Subset covering the entries needed for the
/// WebTransport CONNECT request and for decoding the typical Kestrel HEADERS response.
/// </summary>
internal static class QpackStaticTable
{
    // (index, name, value-or-null-for-name-only)
    private static readonly (int Index, string Name, string? Value)[] StaticEntries =
    {
        (0,  ":authority", null),
        (1,  ":path", "/"),
        (15, ":method", "CONNECT"),
        (16, ":method", "DELETE"),
        (17, ":method", "GET"),
        (18, ":method", "HEAD"),
        (19, ":method", "OPTIONS"),
        (20, ":method", "POST"),
        (21, ":method", "PUT"),
        (22, ":scheme", "http"),
        (23, ":scheme", "https"),
        (24, ":status", "103"),
        (25, ":status", "200"),
        (26, ":status", "304"),
        (27, ":status", "404"),
        (28, ":status", "503"),
        (63, ":status", "100"),
        (64, ":status", "204"),
        (65, ":status", "206"),
        (66, ":status", "302"),
        (67, ":status", "400"),
        (68, ":status", "403"),
        (69, ":status", "421"),
        (70, ":status", "425"),
        (71, ":status", "500"),
        (90, "origin", null),
    };

    public static int GetNameIndex(string headerName)
    {
        foreach ((int index, string name, string? _) in StaticEntries)
            if (string.Equals(name, headerName, StringComparison.Ordinal))
                return index;
        return -1;
    }

    public static int? GetExactIndex(string headerName, string headerValue)
    {
        foreach ((int index, string name, string? value) in StaticEntries)
            if (value != null
                && string.Equals(name, headerName, StringComparison.Ordinal)
                && string.Equals(value, headerValue, StringComparison.Ordinal))
                return index;
        return null;
    }

    public static bool TryLookup(int index, out string name, out string? value)
    {
        foreach ((int candidateIndex, string candidateName, string? candidateValue) in StaticEntries)
        {
            if (candidateIndex == index)
            {
                name = candidateName;
                value = candidateValue;
                return true;
            }
        }
        name = string.Empty;
        value = null;
        return false;
    }
}
