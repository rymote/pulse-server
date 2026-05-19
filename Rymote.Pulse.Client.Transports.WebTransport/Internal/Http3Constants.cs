namespace Rymote.Pulse.Client.Transports.WebTransport.Internal;

internal static class Http3Constants
{
    // HTTP/3 frame types (RFC 9114 §7.2)
    public const ulong FRAME_DATA = 0x00;
    public const ulong FRAME_HEADERS = 0x01;
    public const ulong FRAME_CANCEL_PUSH = 0x03;
    public const ulong FRAME_SETTINGS = 0x04;
    public const ulong FRAME_PUSH_PROMISE = 0x05;
    public const ulong FRAME_GOAWAY = 0x07;
    public const ulong FRAME_MAX_PUSH_ID = 0x0D;

    // HTTP/3 unidirectional stream types (RFC 9114 §6.2)
    public const ulong STREAM_CONTROL = 0x00;
    public const ulong STREAM_PUSH = 0x01;
    public const ulong STREAM_QPACK_ENCODER = 0x02;
    public const ulong STREAM_QPACK_DECODER = 0x03;

    // WebTransport draft over HTTP/3 stream types (draft-ietf-webtrans-http3 §5)
    public const ulong STREAM_WEBTRANSPORT_UNI = 0x54;
    public const ulong FRAME_WEBTRANSPORT_BIDI = 0x41;

    // HTTP/3 SETTINGS identifiers (subset relevant to WebTransport)
    public const ulong SETTING_ENABLE_WEBTRANSPORT = 0x2B603742;
    public const ulong SETTING_H3_DATAGRAM = 0x33;
    public const ulong SETTING_QPACK_MAX_TABLE_CAPACITY = 0x01;
    public const ulong SETTING_QPACK_BLOCKED_STREAMS = 0x07;

    // ALPN for HTTP/3 connections
    public const string ALPN_H3 = "h3";
}
