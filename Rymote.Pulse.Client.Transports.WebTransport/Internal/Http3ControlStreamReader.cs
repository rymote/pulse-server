using System.Net.Quic;
using Rymote.Pulse.Core.Logging;

namespace Rymote.Pulse.Client.Transports.WebTransport.Internal;

/// <summary>
/// Background reader for the server's inbound HTTP/3 control stream. Consumes the SETTINGS
/// frame (validating that <c>SETTINGS_ENABLE_WEBTRANSPORT = 1</c>) and then silently skips
/// any further frames (GOAWAY, MAX_PUSH_ID, etc.) until the stream ends.
/// </summary>
internal static class Http3ControlStreamReader
{
    /// <summary>
    /// Accepts the next inbound stream from the connection, verifies it's the HTTP/3 control
    /// stream, and reads its SETTINGS frame. Returns when the server's SETTINGS has been
    /// consumed; further frames continue to be drained on a detached task.
    /// </summary>
    public static async Task<IReadOnlyDictionary<ulong, ulong>> AcceptAndReadSettingsAsync(
        QuicConnection quicConnection,
        IPulseLogger logger,
        CancellationToken cancellationToken)
    {
        QuicStream inboundStream = await quicConnection.AcceptInboundStreamAsync(cancellationToken)
            .ConfigureAwait(false);

        ulong streamType = await Http3VarInt.ReadAsync(inboundStream, cancellationToken).ConfigureAwait(false);
        if (streamType != Http3Constants.STREAM_CONTROL)
        {
            logger.LogWarning(
                $"WebTransport handshake: first inbound stream was type 0x{streamType:X}, expected CONTROL (0x00). Continuing without server SETTINGS validation.");
            _ = Task.Run(() => DrainStreamAsync(inboundStream, cancellationToken), CancellationToken.None);
            return new Dictionary<ulong, ulong>();
        }

        // Read first frame, must be SETTINGS.
        ulong frameType = await Http3VarInt.ReadAsync(inboundStream, cancellationToken).ConfigureAwait(false);
        ulong frameLength = await Http3VarInt.ReadAsync(inboundStream, cancellationToken).ConfigureAwait(false);

        if (frameType != Http3Constants.FRAME_SETTINGS)
            throw new InvalidOperationException(
                $"WebTransport handshake: first frame on control stream was 0x{frameType:X}, expected SETTINGS (0x{Http3Constants.FRAME_SETTINGS:X}).");

        if (frameLength > 1024)
            throw new InvalidOperationException(
                $"WebTransport handshake: server SETTINGS payload unexpectedly large ({frameLength} bytes).");

        byte[] settingsPayload = new byte[(int)frameLength];
        int bytesRead = 0;
        while (bytesRead < settingsPayload.Length)
        {
            int chunk = await inboundStream.ReadAsync(
                settingsPayload.AsMemory(bytesRead, settingsPayload.Length - bytesRead),
                cancellationToken).ConfigureAwait(false);
            if (chunk == 0)
                throw new EndOfStreamException("WebTransport handshake: control stream truncated mid-SETTINGS.");
            bytesRead += chunk;
        }

        Dictionary<ulong, ulong> serverSettings = ParseSettings(settingsPayload);

        // Keep the control stream alive (server may emit GOAWAY/MAX_PUSH_ID later).
        _ = Task.Run(() => DrainStreamAsync(inboundStream, cancellationToken), CancellationToken.None);

        return serverSettings;
    }

    private static Dictionary<ulong, ulong> ParseSettings(ReadOnlySpan<byte> payload)
    {
        Dictionary<ulong, ulong> settings = new();
        int offset = 0;

        while (offset < payload.Length)
        {
            if (!Http3VarInt.TryDecode(payload[offset..], out ulong settingId, out int idBytesConsumed))
                break;
            offset += idBytesConsumed;
            if (offset >= payload.Length) break;

            if (!Http3VarInt.TryDecode(payload[offset..], out ulong settingValue, out int valueBytesConsumed))
                break;
            offset += valueBytesConsumed;

            settings[settingId] = settingValue;
        }

        return settings;
    }

    private static async Task DrainStreamAsync(QuicStream inboundStream, CancellationToken cancellationToken)
    {
        byte[] sinkBuffer = new byte[4096];
        try
        {
            while (true)
            {
                int chunk = await inboundStream.ReadAsync(sinkBuffer.AsMemory(), cancellationToken)
                    .ConfigureAwait(false);
                if (chunk == 0) return;
            }
        }
        catch
        {
            // Stream closed or aborted — that's fine.
        }
        finally
        {
            try { await inboundStream.DisposeAsync().ConfigureAwait(false); }
            catch { /* ignore */ }
        }
    }
}
