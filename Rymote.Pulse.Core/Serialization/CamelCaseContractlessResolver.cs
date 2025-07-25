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
            Type targetType = typeof(T);

            bool treatAsObject =
                !targetType.IsPrimitive &&
                targetType != typeof(string) &&
                targetType != typeof(decimal) &&
                !typeof(System.Collections.IEnumerable).IsAssignableFrom(targetType);

            if (!treatAsObject) return null;

            Type formatterType = typeof(CamelCaseObjectFormatter<>).MakeGenericType(targetType);
            return (IMessagePackFormatter<T>)Activator.CreateInstance(formatterType)!;
        }
    }

    private sealed class CamelCaseObjectFormatter<T> : IMessagePackFormatter<T>
    {
        private readonly PropertyHandler[] _propertyHandlers;
        private readonly Dictionary<string, PropertyHandler> _lookupString;
        private readonly Dictionary<int, PropertyHandler> _lookupInteger;
        private readonly Func<T> _constructorDelegate;

        public CamelCaseObjectFormatter()
        {
            _constructorDelegate = () => Activator.CreateInstance<T>()!;
            _propertyHandlers = typeof(T)
                .GetProperties(BindingFlags.Instance | BindingFlags.Public)
                .Where(propertyInfo => propertyInfo is { CanRead: true, CanWrite: true })
                .Select(CreateHandler)
                .ToArray();

            _lookupString = _propertyHandlers
                .Where(handler => handler.IntegerKey is null)
                .ToDictionary(handler => handler.StringKey, handler => handler, StringComparer.Ordinal);

            _lookupInteger = _propertyHandlers
                .Where(handler => handler.IntegerKey is not null)
                .ToDictionary(handler => handler.IntegerKey!.Value, handler => handler);
        }

        public void Serialize(ref MessagePackWriter writer, T value, MessagePackSerializerOptions options)
        {
            if (value == null)
            {
                writer.WriteNil();
                return;
            }

            writer.WriteMapHeader(_propertyHandlers.Length);

            foreach (PropertyHandler handler in _propertyHandlers)
            {
                if (handler.IntegerKey is int integerKey)
                    writer.Write(integerKey);
                else
                    writer.Write(handler.StringKey);

                handler.Serialize(ref writer, handler.Getter(value), options);
            }
        }

        public T Deserialize(ref MessagePackReader reader, MessagePackSerializerOptions options)
        {
            if (reader.TryReadNil()) return default!;

            T instance = _constructorDelegate();
            int mapCount = reader.ReadMapHeader();

            for (int index = 0; index < mapCount; index++)
            {
                PropertyHandler? handler = null;
                switch (reader.NextMessagePackType)
                {
                    case MessagePackType.Integer:
                        if (_lookupInteger.TryGetValue(reader.ReadInt32(), out PropertyHandler intHandler))
                            handler = intHandler;
                        else
                            reader.Skip();
                        break;

                    case MessagePackType.String:
                        if (_lookupString.TryGetValue(reader.ReadString(), out PropertyHandler strHandler))
                            handler = strHandler;
                        else
                            reader.Skip();
                        break;

                    default:
                        reader.Skip();
                        break;
                }

                if (handler is null) continue;

                object? propertyValue = handler.Deserialize(ref reader, options);
                handler.Setter(instance, propertyValue);
            }

            return instance;
        }

        private static PropertyHandler CreateHandler(PropertyInfo propertyInfo)
        {
            KeyAttribute? keyAttribute = propertyInfo.GetCustomAttribute<KeyAttribute>();
            int? integerKey = keyAttribute?.IntKey is >= 0 ? keyAttribute.IntKey : null;
            string stringKey = keyAttribute?.StringKey ?? ConvertToCamelCase(propertyInfo.Name);

            MethodInfo getMethod = propertyInfo.GetGetMethod()!;
            MethodInfo setMethod = propertyInfo.GetSetMethod()!;
            Func<T, object?> getterDelegate = (Func<T, object?>)Delegate.CreateDelegate(typeof(Func<T, object?>), null, getMethod);
            Action<T, object?> setterDelegate = (Action<T, object?>)Delegate.CreateDelegate(typeof(Action<T, object?>), null, setMethod);

            IMessagePackFormatter<object?> untypedFormatter = GetUntypedFormatter(propertyInfo.PropertyType);

            return new PropertyHandler(
                stringKey,
                integerKey,
                obj => getterDelegate((T)obj),
                (obj, value) => setterDelegate((T)obj, value),
                (ref MessagePackWriter writer, object? value, MessagePackSerializerOptions options) => untypedFormatter.Serialize(ref writer, value, options),
                (ref MessagePackReader reader, MessagePackSerializerOptions options) => untypedFormatter.Deserialize(ref reader, options));
        }

        private static IMessagePackFormatter<object?> GetUntypedFormatter(Type propertyType)
        {
            MethodInfo genericMethod = typeof(CamelCaseObjectFormatter<T>)
                .GetMethod(nameof(GetFormatterGeneric), BindingFlags.NonPublic | BindingFlags.Static)!;

            MethodInfo closedMethod = genericMethod.MakeGenericMethod(propertyType);
            return (IMessagePackFormatter<object?>)closedMethod.Invoke(null, null)!;
        }

        private static IMessagePackFormatter<object?> GetFormatterGeneric<TProperty>()
        {
            IMessagePackFormatter<TProperty> typedFormatter =
                ContractlessStandardResolver.Instance.GetFormatterWithVerify<TProperty>();

            return new BoxedFormatter<TProperty>(typedFormatter);
        }

        private static string ConvertToCamelCase(string name)
        {
            if (string.IsNullOrEmpty(name)) return name;
            if (name.Length == 1) return name.ToLowerInvariant();
            return char.ToLowerInvariant(name[0]) + name.Substring(1);
        }

        private sealed record PropertyHandler(
            string StringKey,
            int? IntegerKey,
            Func<object, object?> Getter,
            Action<object, object?> Setter,
            ActionRefWriter Serialize,
            FuncRefReader Deserialize);

        private delegate void ActionRefWriter(ref MessagePackWriter writer, object? value, MessagePackSerializerOptions options);
        private delegate object? FuncRefReader(ref MessagePackReader reader, MessagePackSerializerOptions options);

        private sealed class BoxedFormatter<TBox> : IMessagePackFormatter<object?>
        {
            private readonly IMessagePackFormatter<TBox> _innerFormatter;
            public BoxedFormatter(IMessagePackFormatter<TBox> inner) => _innerFormatter = inner;
            public void Serialize(ref MessagePackWriter writer, object? value, MessagePackSerializerOptions options) =>
                _innerFormatter.Serialize(ref writer, value is null ? default! : (TBox)value, options);
            public object? Deserialize(ref MessagePackReader reader, MessagePackSerializerOptions options) =>
                _innerFormatter.Deserialize(ref reader, options);
        }
    }
}