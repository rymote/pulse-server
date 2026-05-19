using System.Text;

namespace Rymote.Pulse.Client.Transports.WebTransport.Internal;

/// <summary>
/// QPACK header-block decoder (RFC 9204). Supports the field-line patterns we expect from
/// a Kestrel HTTP/3 response: indexed fields (static table), literal fields with static-name
/// reference, and literal fields with literal name. Does not support dynamic table lookups
/// (we send Required Insert Count = 0, so the server cannot reference dynamic entries
/// without protocol error). Huffman-encoded names/values are not decoded — they are skipped
/// with a clear "huffman" placeholder so callers see the value isn't available.
/// </summary>
internal static class QpackHeaderDecoder
{
    public static IReadOnlyList<(string Name, string Value)> Decode(ReadOnlySpan<byte> headerBlock)
    {
        List<(string Name, string Value)> results = new();
        int offset = 0;

        // Field Section Prefix: Required Insert Count (8-bit prefix) + Delta Base (7-bit prefix with Sign bit).
        if (offset >= headerBlock.Length)
            return results;
        offset += SkipPrefixedInteger(headerBlock, offset, prefixBits: 8);
        if (offset >= headerBlock.Length)
            return results;
        offset += SkipPrefixedInteger(headerBlock, offset, prefixBits: 7);

        while (offset < headerBlock.Length)
        {
            byte controlByte = headerBlock[offset];

            if ((controlByte & 0x80) != 0)
            {
                // 1Txxxxxx — Indexed Field Line.
                bool isStatic = (controlByte & 0x40) != 0;
                int parsedIndex = DecodePrefixedInteger(headerBlock, ref offset, prefixBits: 6);

                if (isStatic && QpackStaticTable.TryLookup(parsedIndex, out string name, out string? value) && value != null)
                {
                    results.Add((name, value));
                }
                else
                {
                    // Dynamic-table reference (we never sent any) or name-only static entry —
                    // safely ignore.
                }
            }
            else if ((controlByte & 0x40) != 0)
            {
                // 01NTxxxx — Literal Field Line with Name Reference.
                bool isStatic = (controlByte & 0x10) != 0;
                int parsedIndex = DecodePrefixedInteger(headerBlock, ref offset, prefixBits: 4);

                string? referencedName = null;
                if (isStatic && QpackStaticTable.TryLookup(parsedIndex, out string staticName, out _))
                    referencedName = staticName;

                string parsedValue = DecodeLiteralString(headerBlock, ref offset);
                if (referencedName != null)
                    results.Add((referencedName, parsedValue));
            }
            else if ((controlByte & 0x20) != 0)
            {
                // 001NHxxx — Literal Field Line with Literal Name.
                string parsedName = DecodeLiteralStringWithPrefixBits(headerBlock, ref offset, prefixBits: 3);
                string parsedValue = DecodeLiteralString(headerBlock, ref offset);
                results.Add((parsedName, parsedValue));
            }
            else
            {
                // 0001xxxx — Indexed Field Line with Post-Base Index (we never request dynamic),
                // or 0000xxxx — Literal Field Line with Post-Base Name Reference.
                // Both reference the dynamic table, which we never use. Skip the rest of the block
                // defensively rather than risk misinterpreting bytes.
                break;
            }
        }

        return results;
    }

    private static int DecodePrefixedInteger(ReadOnlySpan<byte> source, ref int offset, int prefixBits)
    {
        int prefixMask = (1 << prefixBits) - 1;
        int value = source[offset] & prefixMask;
        offset++;

        if (value < prefixMask) return value;

        int shift = 0;
        while (offset < source.Length)
        {
            byte continuationByte = source[offset++];
            value += (continuationByte & 0x7F) << shift;
            shift += 7;
            if ((continuationByte & 0x80) == 0) break;
        }
        return value;
    }

    private static int SkipPrefixedInteger(ReadOnlySpan<byte> source, int offset, int prefixBits)
    {
        int startOffset = offset;
        DecodePrefixedInteger(source, ref offset, prefixBits);
        return offset - startOffset;
    }

    private static string DecodeLiteralString(ReadOnlySpan<byte> source, ref int offset)
    {
        return DecodeLiteralStringWithPrefixBits(source, ref offset, prefixBits: 7);
    }

    private static string DecodeLiteralStringWithPrefixBits(ReadOnlySpan<byte> source, ref int offset, int prefixBits)
    {
        bool isHuffmanEncoded = (source[offset] & (1 << prefixBits)) != 0;
        int length = DecodePrefixedInteger(source, ref offset, prefixBits);

        if (offset + length > source.Length)
            return string.Empty;

        ReadOnlySpan<byte> payloadSpan = source.Slice(offset, length);
        offset += length;

        if (isHuffmanEncoded)
        {
            // Huffman decoding not implemented — return a marker so callers know the value
            // isn't available. Kestrel's static-indexed `:status` paths avoid Huffman for our
            // request/response shape, so this is a safety fallback rather than a hot path.
            return "<huffman>";
        }

        return Encoding.UTF8.GetString(payloadSpan);
    }
}
