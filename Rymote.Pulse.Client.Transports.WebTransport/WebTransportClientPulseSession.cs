using System.Buffers.Binary;
using System.Net.Quic;
using Rymote.Pulse.Client.Transports.WebTransport.Internal;
using Rymote.Pulse.Core.Transport;

namespace Rymote.Pulse.Client.Transports.WebTransport;

internal sealed class WebTransportClientPulseSession : IPulseSession
{
    public string SessionId { get; }
    public string TransportName => "webtransport";
    public bool IsOpen { get; private set; } = true;
    public IReadOnlyDictionary<string, string> QueryParameters { get; }
    public IReadOnlyDictionary<string, object> InitialMetadata { get; }
    public IPulseDatagramChannel? Datagrams => null;

    private readonly QuicConnection _quicConnection;
    private readonly ulong _wtSessionStreamId;
    private readonly WebTransportClientTransportOptions _options;
    private int _disposed;

    public WebTransportClientPulseSession(
        QuicConnection quicConnection,
        ulong webTransportSessionStreamId,
        WebTransportClientTransportOptions options,
        IReadOnlyDictionary<string, string>? queryParameters = null,
        IReadOnlyDictionary<string, object>? initialMetadata = null)
    {
        _quicConnection = quicConnection;
        _wtSessionStreamId = webTransportSessionStreamId;
        _options = options;
        SessionId = webTransportSessionStreamId.ToString();
        QueryParameters = queryParameters ?? new Dictionary<string, string>();
        InitialMetadata = initialMetadata ?? new Dictionary<string, object>();
    }

    public async ValueTask<IPulseStream?> AcceptStreamAsync(CancellationToken cancellationToken)
    {
        QuicStream? quicStream;
        try
        {
            quicStream = await _quicConnection.AcceptInboundStreamAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (QuicException) { return null; }
        catch (OperationCanceledException) { return null; }
        catch (ObjectDisposedException) { return null; }

        if (quicStream == null) return null;

        // Strip the WebTransport stream prefix: [varint stream-type][varint session-id]
        // for uni, or [varint WT_BIDI_STREAM][varint session-id] for bidi.
        await SkipWebTransportPrefixAsync(quicStream, cancellationToken).ConfigureAwait(false);

        PulseStreamDirection direction = quicStream.CanWrite
            ? PulseStreamDirection.Bidirectional
            : PulseStreamDirection.UnidirectionalServerToClient;

        return new WebTransportClientPulseStream(quicStream, direction, _options.MaxStreamEnvelopeSizeInBytes);
    }

    public async ValueTask<IPulseStream> OpenStreamAsync(PulseStreamDirection direction, CancellationToken cancellationToken)
    {
        if (direction == PulseStreamDirection.UnidirectionalServerToClient)
            throw new InvalidOperationException("Client cannot open a server-to-client uni stream.");

        QuicStreamType quicStreamType = direction == PulseStreamDirection.Bidirectional
            ? QuicStreamType.Bidirectional
            : QuicStreamType.Unidirectional;

        QuicStream quicStream =
            await _quicConnection.OpenOutboundStreamAsync(quicStreamType, cancellationToken).ConfigureAwait(false);

        // Write WebTransport stream prefix.
        byte[] prefixBuffer = new byte[24];
        int offset = 0;
        if (direction == PulseStreamDirection.Bidirectional)
        {
            offset += Http3VarInt.Encode(Http3Constants.FRAME_WEBTRANSPORT_BIDI, prefixBuffer.AsSpan(offset));
        }
        else
        {
            offset += Http3VarInt.Encode(Http3Constants.STREAM_WEBTRANSPORT_UNI, prefixBuffer.AsSpan(offset));
        }
        offset += Http3VarInt.Encode(_wtSessionStreamId, prefixBuffer.AsSpan(offset));

        await quicStream.WriteAsync(prefixBuffer.AsMemory(0, offset), cancellationToken).ConfigureAwait(false);
        await quicStream.FlushAsync(cancellationToken).ConfigureAwait(false);

        return new WebTransportClientPulseStream(quicStream, direction, _options.MaxStreamEnvelopeSizeInBytes);
    }

    public async ValueTask CloseAsync(int reasonCode, TimeSpan drainTimeout, CancellationToken cancellationToken)
    {
        IsOpen = false;
        try
        {
            await _quicConnection.CloseAsync(reasonCode, cancellationToken).ConfigureAwait(false);
        }
        catch { /* best-effort */ }
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        IsOpen = false;

        try { await _quicConnection.DisposeAsync().ConfigureAwait(false); }
        catch { /* best-effort */ }

        GC.SuppressFinalize(this);
    }

    private static async Task SkipWebTransportPrefixAsync(QuicStream quicStream, CancellationToken cancellationToken)
    {
        // Read varint stream-type (or frame type for bidi). Then read varint session-id.
        // We don't validate against our session id here — assumes a single session per connection.
        await Http3VarInt.ReadAsync(quicStream, cancellationToken).ConfigureAwait(false);
        await Http3VarInt.ReadAsync(quicStream, cancellationToken).ConfigureAwait(false);
    }
}
