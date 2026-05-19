namespace Rymote.Pulse.Client.Transports.WebTransport.Internal;

internal static class Http3FrameWriter
{
    public static async Task WriteFrameAsync(
        Stream targetStream,
        ulong frameType,
        ReadOnlyMemory<byte> payload,
        CancellationToken cancellationToken)
    {
        byte[] frameHeader = new byte[16];
        int writtenLength = 0;
        writtenLength += Http3VarInt.Encode(frameType, frameHeader.AsSpan(writtenLength));
        writtenLength += Http3VarInt.Encode((ulong)payload.Length, frameHeader.AsSpan(writtenLength));

        await targetStream.WriteAsync(frameHeader.AsMemory(0, writtenLength), cancellationToken).ConfigureAwait(false);
        if (payload.Length > 0)
            await targetStream.WriteAsync(payload, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// Builds the body of a SETTINGS frame from a list of (id, value) tuples encoded as adjacent varints.
    /// </summary>
    public static byte[] BuildSettingsPayload(IReadOnlyList<(ulong SettingId, ulong SettingValue)> settings)
    {
        int capacity = 0;
        foreach ((ulong settingId, ulong settingValue) in settings)
            capacity += Http3VarInt.EncodedLength(settingId) + Http3VarInt.EncodedLength(settingValue);

        byte[] buffer = new byte[capacity];
        int offset = 0;
        foreach ((ulong settingId, ulong settingValue) in settings)
        {
            offset += Http3VarInt.Encode(settingId, buffer.AsSpan(offset));
            offset += Http3VarInt.Encode(settingValue, buffer.AsSpan(offset));
        }
        return buffer;
    }
}
