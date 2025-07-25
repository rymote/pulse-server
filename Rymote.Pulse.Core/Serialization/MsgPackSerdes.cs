using System.Buffers;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using MessagePack;
using MessagePack.Resolvers;

namespace Rymote.Pulse.Core.Serialization;

public static class MsgPackSerdes
{
    private const int PREFIX_LENGTH = 13;
    private static readonly int BUFFER_WRITER_POOL_SIZE = Environment.ProcessorCount * 2;

    private static readonly MessagePackSerializerOptions _options =
        MessagePackSerializerOptions.Standard
            .WithCompression(MessagePackCompression.None)
            .WithResolver(CompositeResolver.Create(
                BuiltinResolver.Instance,
                AttributeFormatterResolver.Instance,
                
                DynamicEnumAsStringResolver.Instance,
                DynamicGenericResolver.Instance,
                DynamicUnionResolver.Instance,
                DynamicObjectResolver.Instance,
                
                GeneratedMessagePackResolver.Instance,
                ContractlessStandardResolver.Instance
            ));
    
    private static readonly ConcurrentQueue<RandomNumberGenerator> _randomNumberGeneratorPool = new();
    private static readonly ThreadLocal<RandomNumberGenerator> _threadLocalRng = 
        new(() => GetOrCreateRng(), trackAllValues: false);
    
    private static readonly ConcurrentQueue<ArrayBufferWriter<byte>> _bufferWriterPool = new();
    private static readonly ArrayPool<byte> _arrayPool = ArrayPool<byte>.Shared;

    static MsgPackSerdes()
    {
        for (int i = 0; i < BUFFER_WRITER_POOL_SIZE; i++)
        {
            _randomNumberGeneratorPool.Enqueue(RandomNumberGenerator.Create());
            _bufferWriterPool.Enqueue(new ArrayBufferWriter<byte>());
        }
    }

    private static RandomNumberGenerator GetOrCreateRng()
    {
        return _randomNumberGeneratorPool.TryDequeue(out RandomNumberGenerator? randomNumberGenerator) ? randomNumberGenerator : RandomNumberGenerator.Create();
    }

    private static ArrayBufferWriter<byte> GetBufferWriter()
    {
        if (!_bufferWriterPool.TryDequeue(out ArrayBufferWriter<byte>? writer)) 
            return new ArrayBufferWriter<byte>();
        
        writer.Clear();
        return writer;
    }

    private static void ReturnBufferWriter(ArrayBufferWriter<byte> writer)
    {
        if (_bufferWriterPool.Count >= BUFFER_WRITER_POOL_SIZE) return;
        
        writer.Clear();
        _bufferWriterPool.Enqueue(writer);
    }

    public static byte[] Serialize<T>(T obj)
    {
        ArrayBufferWriter<byte> bufferWriter = GetBufferWriter();
        
        try
        {
            MessagePackSerializer.Serialize(bufferWriter, obj, _options);
            
            ReadOnlySpan<byte> serializedData = bufferWriter.WrittenSpan;
            int totalLength = PREFIX_LENGTH + serializedData.Length;
            
            byte[] result = new byte[totalLength];
            
            _threadLocalRng.Value!.GetBytes(result.AsSpan(0, PREFIX_LENGTH));
            
            serializedData.CopyTo(result.AsSpan(PREFIX_LENGTH));
            
            return result;
        }
        finally
        {
            ReturnBufferWriter(bufferWriter);
        }
    }

    public static T Deserialize<T>(byte[] data)
    {
        if (data.Length <= PREFIX_LENGTH)
            throw new ArgumentException("Frame too short", nameof(data));

        ReadOnlyMemory<byte> payload = data.AsMemory(PREFIX_LENGTH);
        return MessagePackSerializer.Deserialize<T>(payload, _options);
    }

    public static unsafe int SerializeToSpan<T>(T obj, Span<byte> destination)
    {
        ArrayBufferWriter<byte> bufferWriter = GetBufferWriter();
        
        try
        {
            MessagePackSerializer.Serialize(bufferWriter, obj, _options);
            
            ReadOnlySpan<byte> serializedData = bufferWriter.WrittenSpan;
            int totalLength = PREFIX_LENGTH + serializedData.Length;
            
            if (destination.Length < totalLength)
                return -totalLength;
            
            _threadLocalRng.Value!.GetBytes(destination.Slice(0, PREFIX_LENGTH));
            serializedData.CopyTo(destination.Slice(PREFIX_LENGTH));
            
            return totalLength;
        }
        finally
        {
            ReturnBufferWriter(bufferWriter);
        }
    }
}