using System.Text;

namespace Rymote.Pulse.Client.Transports.WebTransport.Internal;

/// <summary>
/// Minimal QPACK header-block encoder (RFC 9204) sufficient for the WebTransport extended-CONNECT
/// request used by the Pulse client. Does NOT support dynamic table or Huffman encoding —
/// every value is written as a literal field referencing static-table names when available
/// and as literal name + literal value otherwise.
/// </summary>
internal sealed class QpackHeaderEncoder
{
    private readonly List<byte> _buffer = new();

    public byte[] BuildHeaderBlock(IReadOnlyList<(string Name, string Value)> headers)
    {
        _buffer.Clear();

        // QPACK Required Insert Count = 0 (no dynamic table use). Encoded as 8-bit prefix integer 0.
        _buffer.Add(0x00);
        // Delta Base = 0 with Sign bit clear. Encoded as 7-bit prefix integer 0 (Sign in MSB).
        _buffer.Add(0x00);

        foreach ((string headerName, string headerValue) in headers)
            WriteHeader(headerName, headerValue);

        return _buffer.ToArray();
    }

    private void WriteHeader(string headerName, string headerValue)
    {
        // Look up a static-table entry for known names. Returns -1 if no match.
        int staticNameIndex = QpackStaticTable.GetNameIndex(headerName);

        if (staticNameIndex >= 0)
        {
            int? exactIndex = QpackStaticTable.GetExactIndex(headerName, headerValue);
            if (exactIndex.HasValue)
            {
                WriteIndexedField(exactIndex.Value);
                return;
            }

            WriteLiteralWithStaticNameReference(staticNameIndex, headerValue);
            return;
        }

        WriteLiteralWithLiteralName(headerName, headerValue);
    }

    // 1Txxxxxx  T=1 (static)  xxxxxx = 6-bit prefix int.
    private void WriteIndexedField(int staticIndex)
    {
        byte firstByte = 0xC0; // 11 000000
        WritePrefixedInteger(firstByte, prefixBits: 6, value: (ulong)staticIndex);
    }

    // 01NTxxxx  N=0  T=1 (static)  xxxx = 4-bit prefix name index.
    // Then value as literal: 0Hxxxxxxx length, then bytes.
    private void WriteLiteralWithStaticNameReference(int staticNameIndex, string headerValue)
    {
        byte firstByte = 0x50; // 0101 0000
        WritePrefixedInteger(firstByte, prefixBits: 4, value: (ulong)staticNameIndex);
        WriteLiteralString(headerValue);
    }

    // 001NHnnn  N=0  H=0 (no huffman)  nnn = 3-bit prefix name length.
    // Then name bytes. Then value as 0Hxxxxxxx length + bytes.
    private void WriteLiteralWithLiteralName(string headerName, string headerValue)
    {
        byte firstByte = 0x20; // 0010 0000 — H=0, N=0
        WritePrefixedInteger(firstByte, prefixBits: 3, value: (ulong)headerName.Length);
        byte[] nameBytes = Encoding.ASCII.GetBytes(headerName);
        _buffer.AddRange(nameBytes);
        WriteLiteralString(headerValue);
    }

    private void WriteLiteralString(string value)
    {
        byte firstByte = 0x00; // H=0
        WritePrefixedInteger(firstByte, prefixBits: 7, value: (ulong)value.Length);
        _buffer.AddRange(Encoding.UTF8.GetBytes(value));
    }

    private void WritePrefixedInteger(byte firstByteHigh, int prefixBits, ulong value)
    {
        ulong prefixMask = (1UL << prefixBits) - 1;

        if (value < prefixMask)
        {
            _buffer.Add((byte)(firstByteHigh | (byte)value));
            return;
        }

        _buffer.Add((byte)(firstByteHigh | (byte)prefixMask));
        value -= prefixMask;
        while (value >= 128)
        {
            _buffer.Add((byte)((value & 0x7F) | 0x80));
            value >>= 7;
        }
        _buffer.Add((byte)value);
    }
}
