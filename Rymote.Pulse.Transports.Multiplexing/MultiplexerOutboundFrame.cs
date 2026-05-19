using System.Buffers.Binary;

namespace Rymote.Pulse.Transports.Multiplexing;

internal readonly struct MultiplexerOutboundFrame
{
    public long StreamId { get; }
    public byte Op { get; }
    public ReadOnlyMemory<byte> Payload { get; }

    public MultiplexerOutboundFrame(long streamId, byte op, ReadOnlyMemory<byte> payload)
    {
        StreamId = streamId;
        Op = op;
        Payload = payload;
    }

    public static MultiplexerOutboundFrame Reset(long streamId, int reasonCode)
    {
        byte[] payload = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(payload, (uint)reasonCode);
        return new MultiplexerOutboundFrame(streamId, MultiplexerFrameOps.RESET, payload);
    }
}
