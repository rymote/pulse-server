namespace Rymote.Pulse.Client.Transports.WebTransport.Internal;

/// <summary>
/// HTTP/3 / QUIC variable-length integer encoding (RFC 9000 §16, RFC 9114 §10).
/// First two bits indicate the encoded length: 00 = 1 byte (6-bit value),
/// 01 = 2 bytes (14-bit value), 10 = 4 bytes (30-bit value), 11 = 8 bytes (62-bit value).
/// </summary>
internal static class Http3VarInt
{
    public static int Encode(ulong value, Span<byte> destination)
    {
        if (value < 1UL << 6)
        {
            destination[0] = (byte)value;
            return 1;
        }
        if (value < 1UL << 14)
        {
            destination[0] = (byte)(0x40 | (byte)(value >> 8));
            destination[1] = (byte)value;
            return 2;
        }
        if (value < 1UL << 30)
        {
            destination[0] = (byte)(0x80 | (byte)(value >> 24));
            destination[1] = (byte)(value >> 16);
            destination[2] = (byte)(value >> 8);
            destination[3] = (byte)value;
            return 4;
        }
        if (value < 1UL << 62)
        {
            destination[0] = (byte)(0xC0 | (byte)(value >> 56));
            destination[1] = (byte)(value >> 48);
            destination[2] = (byte)(value >> 40);
            destination[3] = (byte)(value >> 32);
            destination[4] = (byte)(value >> 24);
            destination[5] = (byte)(value >> 16);
            destination[6] = (byte)(value >> 8);
            destination[7] = (byte)value;
            return 8;
        }
        throw new ArgumentOutOfRangeException(nameof(value), "Value exceeds 62-bit varint maximum.");
    }

    public static int EncodedLength(ulong value)
    {
        if (value < 1UL << 6) return 1;
        if (value < 1UL << 14) return 2;
        if (value < 1UL << 30) return 4;
        return 8;
    }

    public static bool TryDecode(ReadOnlySpan<byte> source, out ulong value, out int bytesConsumed)
    {
        value = 0;
        bytesConsumed = 0;
        if (source.Length == 0) return false;

        int length = 1 << ((source[0] & 0xC0) >> 6);
        if (source.Length < length) return false;

        value = (ulong)(source[0] & 0x3F);
        for (int index = 1; index < length; index++)
            value = (value << 8) | source[index];

        bytesConsumed = length;
        return true;
    }

    public static async ValueTask<ulong> ReadAsync(Stream stream, CancellationToken cancellationToken)
    {
        byte[] firstByte = new byte[1];
        if (!await ReadExactlyAsync(stream, firstByte, cancellationToken).ConfigureAwait(false))
            throw new EndOfStreamException("VarInt: stream ended before first byte.");

        int length = 1 << ((firstByte[0] & 0xC0) >> 6);
        ulong value = (ulong)(firstByte[0] & 0x3F);

        if (length > 1)
        {
            byte[] rest = new byte[length - 1];
            if (!await ReadExactlyAsync(stream, rest, cancellationToken).ConfigureAwait(false))
                throw new EndOfStreamException("VarInt: stream ended mid-encoding.");
            foreach (byte streamByte in rest)
                value = (value << 8) | streamByte;
        }

        return value;
    }

    private static async ValueTask<bool> ReadExactlyAsync(Stream stream, byte[] buffer, CancellationToken cancellationToken)
    {
        int bytesRead = 0;
        while (bytesRead < buffer.Length)
        {
            int chunk = await stream.ReadAsync(
                buffer.AsMemory(bytesRead, buffer.Length - bytesRead), cancellationToken).ConfigureAwait(false);
            if (chunk == 0) return false;
            bytesRead += chunk;
        }
        return true;
    }
}
