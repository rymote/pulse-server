using System.Buffers;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using MessagePack;
using MessagePack.Resolvers;

namespace Rymote.Pulse.Core.Serialization;

public static class MsgPackSerdes
{
    private const int NONCE_LENGTH = 12;
    private const int TAG_LENGTH = 16;
    private const int PREFIX_LENGTH = NONCE_LENGTH;

    private static readonly int BUFFER_WRITER_POOL_SIZE = Environment.ProcessorCount * 2;

    private static readonly MessagePackSerializerOptions _options =
        MessagePackSerializerOptions.Standard
            .WithCompression(MessagePackCompression.None)
            .WithResolver(CompositeResolver.Create(
                AttributeFormatterResolver.Instance,
                GeneratedMessagePackResolver.Instance,
                BuiltinResolver.Instance,
                CamelCaseContractlessResolver.Instance,
                DynamicEnumAsStringResolver.Instance,
                DynamicGenericResolver.Instance,
                DynamicUnionResolver.Instance,
                DynamicObjectResolver.Instance,
                ContractlessStandardResolver.Instance
            ));

    private static readonly ConcurrentQueue<RandomNumberGenerator> _randomNumberGeneratorPool = new();
    private static readonly ThreadLocal<RandomNumberGenerator> _threadLocalRng =
        new(() => GetOrCreateRng(), trackAllValues: false);

    private static readonly ConcurrentQueue<ArrayBufferWriter<byte>> _bufferWriterPool = new();
    private static readonly byte[] _key = Convert.FromHexString("f3b4d89e3a6c2b75a1ee4c6d90f8a2134dd89e3aa1bc67423ff4b62184ce93de");

    static MsgPackSerdes()
    {
        for (int index = 0; index < BUFFER_WRITER_POOL_SIZE; index++)
        {
            _randomNumberGeneratorPool.Enqueue(RandomNumberGenerator.Create());
            _bufferWriterPool.Enqueue(new ArrayBufferWriter<byte>());
        }
    }

    private static RandomNumberGenerator GetOrCreateRng()
    {
        return _randomNumberGeneratorPool.TryDequeue(out RandomNumberGenerator? generator)
            ? generator
            : RandomNumberGenerator.Create();
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

            ReadOnlySpan<byte> plaintext = bufferWriter.WrittenSpan;

            Span<byte> nonce = stackalloc byte[NONCE_LENGTH];
            _threadLocalRng.Value!.GetBytes(nonce);

            byte[] ciphertext = new byte[plaintext.Length];
            Span<byte> tag = stackalloc byte[TAG_LENGTH];

            using ChaCha20Poly1305 cipher = new ChaCha20Poly1305(_key);
            cipher.Encrypt(nonce, plaintext, ciphertext, tag);

            byte[] result = new byte[NONCE_LENGTH + TAG_LENGTH + ciphertext.Length];
            nonce.CopyTo(result.AsSpan(0, NONCE_LENGTH));
            ciphertext.CopyTo(result.AsSpan(NONCE_LENGTH));
            tag.CopyTo(result.AsSpan(NONCE_LENGTH + ciphertext.Length));

            return result;
        }
        finally
        {
            ReturnBufferWriter(bufferWriter);
        }
    }

    public static T Deserialize<T>(byte[] data)
    {
        if (data.Length <= NONCE_LENGTH + TAG_LENGTH)
            throw new ArgumentException("Frame too short", nameof(data));

        ReadOnlySpan<byte> nonce = data.AsSpan(0, NONCE_LENGTH);
        ReadOnlySpan<byte> tag = data.AsSpan(data.Length - TAG_LENGTH, TAG_LENGTH);
        ReadOnlySpan<byte> ciphertext = data.AsSpan(NONCE_LENGTH, data.Length - NONCE_LENGTH - TAG_LENGTH);

        byte[] plaintext = new byte[ciphertext.Length];

        using ChaCha20Poly1305 cipher = new ChaCha20Poly1305(_key);
        cipher.Decrypt(nonce, ciphertext, tag, plaintext);

        return MessagePackSerializer.Deserialize<T>(plaintext, _options);
    }

    public static int SerializeToSpan<T>(T obj, Span<byte> destination)
    {
        ArrayBufferWriter<byte> bufferWriter = GetBufferWriter();

        try
        {
            MessagePackSerializer.Serialize(bufferWriter, obj, _options);

            ReadOnlySpan<byte> plaintext = bufferWriter.WrittenSpan;

            int totalLength = NONCE_LENGTH + TAG_LENGTH + plaintext.Length;
            if (destination.Length < totalLength)
                return -totalLength;

            Span<byte> nonce = destination.Slice(0, NONCE_LENGTH);
            _threadLocalRng.Value!.GetBytes(nonce);

            Span<byte> ciphertext = destination.Slice(NONCE_LENGTH, plaintext.Length);
            Span<byte> tag = destination.Slice(NONCE_LENGTH + plaintext.Length, TAG_LENGTH);

            using ChaCha20Poly1305 cipher = new ChaCha20Poly1305(_key);
            cipher.Encrypt(nonce, plaintext, ciphertext, tag);

            return totalLength;
        }
        finally
        {
            ReturnBufferWriter(bufferWriter);
        }
    }
}
