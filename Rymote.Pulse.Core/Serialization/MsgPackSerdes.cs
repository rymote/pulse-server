using MessagePack;
using MessagePack.Resolvers;

namespace Rymote.Pulse.Core.Serialization;

public static class MsgPackSerdes
{
    private static readonly MessagePackSerializerOptions _options =
        MessagePackSerializerOptions.Standard
            .WithCompression(MessagePackCompression.None)
            .WithResolver(CompositeResolver.Create(
                GeneratedMessagePackResolver.Instance,
                ContractlessStandardResolver.Instance
            ));
    
    public static byte[] Serialize<T>(T obj) => MessagePackSerializer.Serialize(obj, _options);

    public static T Deserialize<T>(byte[] data) => MessagePackSerializer.Deserialize<T>(data, _options);
}