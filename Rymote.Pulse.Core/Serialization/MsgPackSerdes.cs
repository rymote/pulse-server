using System.Security.Cryptography;
using MessagePack;
using MessagePack.Resolvers;

namespace Rymote.Pulse.Core.Serialization;

public static class MsgPackSerdes
{
    private const int PREFIX_LENGTH = 13;

    private static readonly MessagePackSerializerOptions _options =
        MessagePackSerializerOptions.Standard
            .WithCompression(MessagePackCompression.None)
            .WithResolver(CompositeResolver.Create(
                GeneratedMessagePackResolver.Instance,
                ContractlessStandardResolver.Instance
            ));

    public static byte[] Serialize<T>(T obj)
    {
        byte[] data = MessagePackSerializer.Serialize(obj, _options);
        byte[] prefix = new byte[PREFIX_LENGTH];
        RandomNumberGenerator.Fill(prefix);

        byte[] result = new byte[PREFIX_LENGTH + data.Length];
        Buffer.BlockCopy(prefix, 0, result, 0, PREFIX_LENGTH);
        Buffer.BlockCopy(data, 0, result, PREFIX_LENGTH, data.Length);
        return result;
    }

    public static T Deserialize<T>(byte[] data)
    {
        if (data.Length <= PREFIX_LENGTH)
            throw new ArgumentException("Frame too short", nameof(data));

        byte[] payload = new byte[data.Length - PREFIX_LENGTH];
        Buffer.BlockCopy(data, PREFIX_LENGTH, payload, 0, payload.Length);
        return MessagePackSerializer.Deserialize<T>(payload, _options);
    }
}