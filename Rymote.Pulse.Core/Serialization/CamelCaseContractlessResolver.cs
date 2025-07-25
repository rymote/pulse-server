using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using MessagePack;
using MessagePack.Formatters;
using MessagePack.Resolvers;

namespace Rymote.Pulse.Core.Serialization;

public sealed class CamelCaseContractlessResolver : IFormatterResolver
{
    public static readonly IFormatterResolver Instance = new CamelCaseContractlessResolver();
    private CamelCaseContractlessResolver() { }

    public IMessagePackFormatter<T>? GetFormatter<T>() => FormatterCache<T>.Formatter;

    private static class FormatterCache<T>
    {
        public static readonly IMessagePackFormatter<T>? Formatter = CreateFormatter();

        private static IMessagePackFormatter<T>? CreateFormatter()
        {
            if (AttributeFormatterResolver.Instance.GetFormatter<T>() != null) return null;
            if (GeneratedMessagePackResolver.Instance.GetFormatter<T>() != null) return null;

            Type targetType = typeof(T);

            bool treatAsObject =
                !targetType.IsPrimitive &&
                targetType != typeof(string) &&
                targetType != typeof(decimal) &&
                !typeof(System.Collections.IEnumerable).IsAssignableFrom(targetType);

            if (!treatAsObject) return null;

            Type formatterType = typeof(CamelCaseFormatter<>).MakeGenericType(targetType);
            return (IMessagePackFormatter<T>)Activator.CreateInstance(formatterType)!;
        }
    }

    private sealed class CamelCaseFormatter<T> : IMessagePackFormatter<T>
    {
        private readonly PropertyInfo[] propertyInfoArray;
        private readonly Dictionary<string, PropertyInfo> stringKeyLookup;
        private readonly Dictionary<int, PropertyInfo> integerKeyLookup;
        private readonly Func<T> objectFactory;

        public CamelCaseFormatter()
        {
            objectFactory = () => Activator.CreateInstance<T>();
            propertyInfoArray = typeof(T)
                .GetProperties(BindingFlags.Instance | BindingFlags.Public)
                .Where(propertyInfo => propertyInfo.CanRead && propertyInfo.CanWrite)
                .ToArray();

            stringKeyLookup = new Dictionary<string, PropertyInfo>(StringComparer.Ordinal);
            integerKeyLookup = new Dictionary<int, PropertyInfo>();

            foreach (PropertyInfo propertyInfo in propertyInfoArray)
            {
                KeyAttribute? keyAttribute = propertyInfo.GetCustomAttribute<KeyAttribute>();
                if (keyAttribute is { IntKey: >= 0 })
                {
                    integerKeyLookup[keyAttribute.IntKey.Value] = propertyInfo;
                }
                else
                {
                    string keyString = keyAttribute?.StringKey ?? ConvertToCamelCase(propertyInfo.Name);
                    stringKeyLookup[keyString] = propertyInfo;
                }
            }
        }

        public void Serialize(ref MessagePackWriter writer, T value, MessagePackSerializerOptions options)
        {
            if (value == null)
            {
                writer.WriteNil();
                return;
            }

            writer.WriteMapHeader(propertyInfoArray.Length);

            foreach (PropertyInfo propertyInfo in propertyInfoArray)
            {
                KeyAttribute? keyAttribute = propertyInfo.GetCustomAttribute<KeyAttribute>();
                if (keyAttribute is { IntKey: >= 0 })
                {
                    writer.Write(keyAttribute.IntKey.Value);
                }
                else
                {
                    writer.Write(keyAttribute?.StringKey ?? ConvertToCamelCase(propertyInfo.Name));
                }

                object? propertyValue = propertyInfo.GetValue(value);
                MessagePackSerializer.Serialize(propertyInfo.PropertyType, ref writer, propertyValue, options);
            }
        }

        public T Deserialize(ref MessagePackReader reader, MessagePackSerializerOptions options)
        {
            if (reader.TryReadNil()) return default!;

            T instance = objectFactory();
            int mapCount = reader.ReadMapHeader();

            for (int mapIndex = 0; mapIndex < mapCount; mapIndex++)
            {
                PropertyInfo? targetPropertyInfo = null;

                switch (reader.NextMessagePackType)
                {
                    case MessagePackType.Integer:
                        int integerKey = reader.ReadInt32();
                        integerKeyLookup.TryGetValue(integerKey, out targetPropertyInfo);
                        break;

                    case MessagePackType.String:
                        string stringKey = reader.ReadString();
                        stringKeyLookup.TryGetValue(stringKey, out targetPropertyInfo);
                        break;

                    default:
                        reader.Skip();
                        continue;
                }

                if (targetPropertyInfo == null)
                {
                    reader.Skip();
                    continue;
                }

                object? propertyValue = MessagePackSerializer.Deserialize(targetPropertyInfo.PropertyType, ref reader, options);
                targetPropertyInfo.SetValue(instance, propertyValue);
            }

            return instance;
        }

        private static string ConvertToCamelCase(string name)
        {
            if (string.IsNullOrEmpty(name) || char.IsLower(name[0])) return name;
            return char.ToLowerInvariant(name[0]) + name.Substring(1);
        }
    }
}
