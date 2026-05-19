namespace Rymote.Pulse.Transports.Multiplexing;

internal static class MultiplexerFrameOps
{
    public const byte OPEN_BIDI = 0x01;
    public const byte OPEN_UNI = 0x02;
    public const byte DATA = 0x03;
    public const byte FIN = 0x04;
    public const byte RESET = 0x05;
    public const byte DATAGRAM = 0x10;
    public const byte PING = 0x20;
    public const byte PONG = 0x21;
    public const byte GOAWAY = 0x22;
}
