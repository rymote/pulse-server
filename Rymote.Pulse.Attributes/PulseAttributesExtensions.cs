using System.Collections.Concurrent;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Rymote.Pulse.Core;
using Rymote.Pulse.Core.Connections;
using Rymote.Pulse.Core.Metadata;

namespace Rymote.Pulse.Attributes;

public static class PulseAttributesExtensions
{
    private class PulseMetadataHandlerConfig
    {
        public string? Key { get; set; }
        public MetadataChangeTypes ChangeTypes { get; set; }
        public Func<PulseConnection, PulseMetadataChangedEventArgs, Task> Handler { get; set; } = null!;
    }

    private static readonly ConcurrentDictionary<string, PulseMetadataSubscriptionTracker> _subscriptionTrackers = new();

    public static void RegisterHandlersFromAssembly(this PulseDispatcher pulseDispatcher, Assembly assembly,
        IServiceProvider? serviceProvider = null)
    {
        List<Type> handlerTypes = assembly.GetTypes()
            .Where(type => type is { IsClass: true, IsAbstract: false })
            .Where(type => type.GetMethods().Any(method =>
                method.GetCustomAttribute<PulseEventAttribute>() != null ||
                method.GetCustomAttribute<PulseRpcAttribute>() != null ||
                method.GetCustomAttribute<PulseStreamAttribute>() != null ||
                method.GetCustomAttribute<PulseOnConnectAttribute>() != null ||
                method.GetCustomAttribute<PulseOnDisconnectAttribute>() != null ||
                method.GetCustomAttribute<PulseMetadataChangedAttribute>() != null))
            .ToList();

        List<PulseMetadataHandlerConfig> metadataHandlerConfigs = [];

        foreach (Type handlerType in handlerTypes)
        {
            object? handlerInstance = serviceProvider != null
                ? ActivatorUtilities.CreateInstance(serviceProvider, handlerType)
                : Activator.CreateInstance(handlerType);

            foreach (MethodInfo method in handlerType.GetMethods(
                         BindingFlags.Instance |
                         BindingFlags.Public |
                         BindingFlags.NonPublic))
            {
                RegisterEventHandler(pulseDispatcher, method, handlerInstance);
                RegisterRpcHandler(pulseDispatcher, method, handlerInstance);
                RegisterOnConnectHandler(pulseDispatcher, method, handlerInstance);
                RegisterOnDisconnectHandler(pulseDispatcher, method, handlerInstance);
                // RegisterStreamHandler(pulseDispatcher, method, handlerInstance);

                CollectMetadataEventHandlers(method, handlerInstance, metadataHandlerConfigs);
            }
        }

        if (metadataHandlerConfigs.Count <= 0) return;

        pulseDispatcher.AddOnConnectHandler(connection =>
        {
            PulseMetadataSubscriptionTracker tracker = new PulseMetadataSubscriptionTracker();
            _subscriptionTrackers[connection.ConnectionId] = tracker;
            
            foreach (PulseMetadataHandlerConfig config in metadataHandlerConfigs)
            {
                PulseMetadataChangedEventHandler wrappedHandler = async (sender, args) =>
                {
                    bool shouldHandle = config.ChangeTypes.HasFlag(MetadataChangeTypes.ALL);

                    if (!shouldHandle)
                    {
                        shouldHandle = (args.ChangeType == PulseMetadataChangeType.CREATED &&
                                        config.ChangeTypes.HasFlag(MetadataChangeTypes.CREATED)) ||
                                       (args.ChangeType == PulseMetadataChangeType.MODIFIED &&
                                        config.ChangeTypes.HasFlag(MetadataChangeTypes.MODIFIED)) ||
                                       (args.ChangeType == PulseMetadataChangeType.DELETED &&
                                        config.ChangeTypes.HasFlag(MetadataChangeTypes.DELETED));
                    }

                    if (!shouldHandle) return;

                    await config.Handler(connection, args);
                };

                if (config.Key != null)
                    connection.Metadata.Subscribe(config.Key, wrappedHandler);
                else
                    connection.Metadata.SubscribeGlobal(wrappedHandler);
                
                tracker.AddSubscription(config.Key, wrappedHandler);
            }

            return Task.CompletedTask;
        });
        
        pulseDispatcher.AddOnDisconnectHandler(connection =>
        {
            if (_subscriptionTrackers.TryRemove(connection.ConnectionId, out PulseMetadataSubscriptionTracker? tracker))
                tracker.UnsubscribeAll(connection);
            
            return Task.CompletedTask;
        });
    }

    private static void RegisterEventHandler(PulseDispatcher pulseDispatcher, MethodInfo method,
        object? handlerInstance)
    {
        if (handlerInstance == null) return;

        PulseEventAttribute? attribute = method.GetCustomAttribute<PulseEventAttribute>();
        if (attribute == null) return;

        ParameterInfo[] parameters = method.GetParameters();
        if (method.ReturnType != typeof(Task)) return;

        string handle = attribute.Handle ?? method.Name;

        if (parameters.Length == 1 && parameters[0].ParameterType == typeof(PulseContext))
        {
            MethodInfo mapEventMethod = typeof(PulseDispatcher)
                .GetMethods()
                .Single(methodInfo => methodInfo.Name == nameof(PulseDispatcher.MapEvent)
                                      && !methodInfo.IsGenericMethodDefinition
                                      && methodInfo.GetParameters().Length == 3
                                      && methodInfo.GetParameters()[0].ParameterType == typeof(string)
                                      && methodInfo.GetParameters()[1].ParameterType == typeof(Func<PulseContext, Task>)
                                      && methodInfo.GetParameters()[2].ParameterType == typeof(string));

            Type delegateType = typeof(Func<,>).MakeGenericType(typeof(PulseContext), typeof(Task));
            Delegate handlerDelegate = Delegate.CreateDelegate(delegateType, handlerInstance, method);

            mapEventMethod.Invoke(pulseDispatcher, new object[] { handle, handlerDelegate, "v1" });
        }
        else if (parameters.Length == 2 && parameters[1].ParameterType == typeof(PulseContext))
        {
            Type payloadType = parameters[0].ParameterType;

            MethodInfo mapEventMethod = typeof(PulseDispatcher)
                .GetMethods()
                .Single(methodInfo => methodInfo.Name == nameof(PulseDispatcher.MapEvent)
                                      && methodInfo.IsGenericMethodDefinition
                                      && methodInfo.GetGenericArguments().Length == 1)
                .MakeGenericMethod(payloadType);

            Type delegateType = typeof(Func<,,>).MakeGenericType(payloadType, typeof(PulseContext), typeof(Task));
            Delegate handlerDelegate = Delegate.CreateDelegate(delegateType, handlerInstance, method);

            mapEventMethod.Invoke(pulseDispatcher, new object[] { handle, handlerDelegate, "v1" });
        }
    }

    private static void RegisterRpcHandler(PulseDispatcher pulseDispatcher, MethodInfo method, object? handlerInstance)
    {
        if (handlerInstance == null) return;

        PulseRpcAttribute? attribute = method.GetCustomAttribute<PulseRpcAttribute>();
        if (attribute == null) return;

        ParameterInfo[] parameters = method.GetParameters();
        if (!method.ReturnType.IsGenericType || method.ReturnType.GetGenericTypeDefinition() != typeof(Task<>)) return;

        Type responseType = method.ReturnType.GetGenericArguments()[0];
        string handle = attribute.Handle ?? method.Name;

        if (parameters.Length == 1 && parameters[0].ParameterType == typeof(PulseContext))
        {
            MethodInfo mapRpcMethod = typeof(PulseDispatcher)
                .GetMethods()
                .Single(methodInfo => methodInfo.Name == nameof(PulseDispatcher.MapRpc)
                                      && methodInfo.IsGenericMethodDefinition
                                      && methodInfo.GetGenericArguments().Length == 1)
                .MakeGenericMethod(responseType);

            Type delegateType =
                typeof(Func<,>).MakeGenericType(typeof(PulseContext), typeof(Task<>).MakeGenericType(responseType));
            Delegate handlerDelegate = Delegate.CreateDelegate(delegateType, handlerInstance, method);

            mapRpcMethod.Invoke(pulseDispatcher, new object[] { handle, handlerDelegate, "v1" });
        }
        else if (parameters.Length == 2 && parameters[1].ParameterType == typeof(PulseContext))
        {
            Type requestType = parameters[0].ParameterType;

            MethodInfo mapRpcMethod = typeof(PulseDispatcher)
                .GetMethods()
                .Single(methodInfo => methodInfo.Name == nameof(PulseDispatcher.MapRpc)
                                      && methodInfo.IsGenericMethodDefinition
                                      && methodInfo.GetGenericArguments().Length == 2)
                .MakeGenericMethod(requestType, responseType);

            Type delegateType = typeof(Func<,,>).MakeGenericType(requestType, typeof(PulseContext),
                typeof(Task<>).MakeGenericType(responseType));
            Delegate handlerDelegate = Delegate.CreateDelegate(delegateType, handlerInstance, method);

            mapRpcMethod.Invoke(pulseDispatcher, new object[] { handle, handlerDelegate, "v1" });
        }
    }

    private static void RegisterOnConnectHandler(PulseDispatcher pulseDispatcher, MethodInfo method,
        object? handlerInstance)
    {
        if (handlerInstance == null) return;

        PulseOnConnectAttribute? attribute = method.GetCustomAttribute<PulseOnConnectAttribute>();
        if (attribute == null) return;

        ParameterInfo[] parameters = method.GetParameters();
        if (method.ReturnType != typeof(Task) || parameters.Length != 1 ||
            parameters[0].ParameterType != typeof(PulseConnection)) return;

        Func<PulseConnection, Task> handler = (Func<PulseConnection, Task>)Delegate.CreateDelegate(
            typeof(Func<PulseConnection, Task>), handlerInstance, method);

        pulseDispatcher.AddOnConnectHandler(handler);
    }

    private static void RegisterOnDisconnectHandler(PulseDispatcher pulseDispatcher, MethodInfo method,
        object? handlerInstance)
    {
        if (handlerInstance == null) return;

        PulseOnDisconnectAttribute? attribute = method.GetCustomAttribute<PulseOnDisconnectAttribute>();
        if (attribute == null) return;

        ParameterInfo[] parameters = method.GetParameters();
        if (method.ReturnType != typeof(Task) || parameters.Length != 1 ||
            parameters[0].ParameterType != typeof(PulseConnection)) return;

        Func<PulseConnection, Task> handler = (Func<PulseConnection, Task>)Delegate.CreateDelegate(
            typeof(Func<PulseConnection, Task>), handlerInstance, method);

        pulseDispatcher.AddOnDisconnectHandler(handler);
    }

    private static void CollectMetadataEventHandlers(MethodInfo method, object? handlerInstance,
        List<PulseMetadataHandlerConfig> configs)
    {
        if (handlerInstance == null) return;

        IEnumerable<PulseMetadataChangedAttribute> attributes =
            method.GetCustomAttributes<PulseMetadataChangedAttribute>();
        if (!attributes.Any()) return;

        ParameterInfo[] parameters = method.GetParameters();
        if (method.ReturnType != typeof(Task) || parameters.Length != 2 ||
            parameters[0].ParameterType != typeof(PulseConnection) ||
            parameters[1].ParameterType != typeof(PulseMetadataChangedEventArgs)) return;

        configs.AddRange(attributes.Select(attribute => new PulseMetadataHandlerConfig
        {
            Key = attribute.Key, ChangeTypes = attribute.ChangeTypes,
            Handler = async (connection, args) => { await (Task)method.Invoke(handlerInstance, [connection, args])!; }
        }));
    }
}