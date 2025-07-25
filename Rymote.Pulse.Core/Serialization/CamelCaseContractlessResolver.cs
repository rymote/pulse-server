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
        private readonly Func<T> _constructorDelegate;

        public CamelCaseObjectFormatter()
        {
            _constructorDelegate = () => Activator.CreateInstance<T>()!;
            _propertyHandlers = typeof(T)
                .GetProperties(BindingFlags.Instance | BindingFlags.Public)
                .Where(propertyInfo => propertyInfo is { CanRead: true, CanWrite: true })
                .Select(CreateHandler)
                .ToArray();
        }

        public void Serialize(ref MessagePackWriter writer, T value, MessagePackSerializerOptions options)
        {
            if (value is null) { writer.WriteNil(); return; }

            writer.WriteMapHeader(_propertyHandlers.Length);

            foreach (PropertyHandler handler in _propertyHandlers)
            {
                writer.Write(handler.CamelCaseName);
                handler.Serialize(ref writer, handler.Getter(value), options);
            }
        }

        public T Deserialize(ref MessagePackReader reader, MessagePackSerializerOptions options)
        {
            if (reader.TryReadNil()) return default!;

            T instance = _constructorDelegate();
            int count = reader.ReadMapHeader();

            for (int index = 0; index < count; index++)
            {
                string? key = reader.ReadString();
                PropertyHandler? handler = _propertyHandlers.FirstOrDefault(propertyHandler => propertyHandler.CamelCaseName == key);
                if (handler is null) { reader.Skip(); continue; }

                object? propertyValue = handler.Deserialize(ref reader, options);
                if (instance != null) handler.Setter(instance, propertyValue);
            }

            return instance;
        }

        private static PropertyHandler CreateHandler(PropertyInfo propertyInfo)
        {
            Type propertyType = propertyInfo.PropertyType;
            string camelCase = Char.ToLowerInvariant(propertyInfo.Name[0]) + propertyInfo.Name.Substring(1);

            MethodInfo getMethod = propertyInfo.GetGetMethod()!;
            MethodInfo setMethod = propertyInfo.GetSetMethod()!;
            Func<T, object?> getter = (Func<T, object?>)Delegate.CreateDelegate(typeof(Func<T, object?>), null, getMethod);
            Action<T, object?> setter = (Action<T, object?>)Delegate.CreateDelegate(typeof(Action<T, object?>), null, setMethod);

            IMessagePackFormatter<object?> untypedFormatter = GetUntypedFormatter(propertyType);
            
            return new PropertyHandler(
                camelCase,
                value => getter((T)value),
                (obj, value) => setter((T)obj, value),
                (ref MessagePackWriter writer, object? value, MessagePackSerializerOptions options) => untypedFormatter.Serialize(ref writer, value, options),
                (ref MessagePackReader reader, MessagePackSerializerOptions options) => untypedFormatter.Deserialize(ref reader, options));
        }

        private static IMessagePackFormatter<object?> GetUntypedFormatter(Type t)
        {
            MethodInfo generic = typeof(CamelCaseObjectFormatter<T>)
                .GetMethod(nameof(GetFormatterGeneric), BindingFlags.NonPublic | BindingFlags.Static)!;

            MethodInfo closed = generic.MakeGenericMethod(t);
            return (IMessagePackFormatter<object?>)closed.Invoke(null, null)!;
        }
        
        private static IMessagePackFormatter<object?> GetFormatterGeneric<TProp>()
        {
            IMessagePackFormatter<TProp> typed =
                ContractlessStandardResolver.Instance.GetFormatterWithVerify<TProp>();

            return new BoxedFormatter<TProp>(typed);
        }
        
        private sealed record PropertyHandler(
            string CamelCaseName,
            Func<object, object?> Getter,
            Action<object, object?> Setter,
            ActionRefWriter Serialize,
            FuncRefReader Deserialize);

        private delegate void ActionRefWriter(ref MessagePackWriter writer, object? value, MessagePackSerializerOptions options);
        private delegate object? FuncRefReader(ref MessagePackReader reader, MessagePackSerializerOptions options);

        internal sealed class BoxedFormatter<TBox> : IMessagePackFormatter<object?>
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