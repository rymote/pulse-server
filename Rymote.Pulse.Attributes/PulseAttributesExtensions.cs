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
        public string? Key { get; init; }
        public MetadataChangeTypes ChangeTypes { get; init; }
        public Func<PulseConnection, PulseMetadataChangedEventArgs, Task> Handler { get; init; } = null!;
    }

    private static readonly ConcurrentDictionary<string, PulseMetadataSubscriptionTracker>
        _subscriptionTrackers = new();

    public static void RegisterHandlersFromAssembly(this PulseDispatcher pulseDispatcher, Assembly assembly,
        IServiceProvider? serviceProvider = null)
    {
        List<Type> handlerTypes = assembly.GetTypes()
            .Where(type => type is { IsClass: true, IsAbstract: false })
            .Where(type => type.GetMethods().Any(method =>
                method.GetCustomAttribute<PulseEventAttribute>() != null ||
                method.GetCustomAttribute<PulseRpcAttribute>() != null ||
                method.GetCustomAttribute<PulseOnConnectAttribute>() != null ||
                method.GetCustomAttribute<PulseOnDisconnectAttribute>() != null ||
                method.GetCustomAttribute<PulseMetadataChangedAttribute>() != null))
            .ToList();

        List<PulseMetadataHandlerConfig> metadataHandlerConfigs = [];

        if (serviceProvider != null)
        {
            pulseDispatcher.Use(async (context, next) =>
            {
                if (!context.Connection.Metadata.ContainsKey("__scope"))
                {
                    using IServiceScope scope = serviceProvider.CreateScope();
                    context.Connection.SetMetadata("__scope", scope);

                    try
                    {
                        await next();
                    }
                    finally
                    {
                        context.Connection.RemoveMetadata("__scope");
                    }
                }
                else
                {
                    await next();
                }
            });
        }

        foreach (Type handlerType in handlerTypes)
        {
            foreach (MethodInfo method in handlerType.GetMethods(
                         BindingFlags.Instance |
                         BindingFlags.Public |
                         BindingFlags.NonPublic))
            {
                RegisterEventHandler(pulseDispatcher, method, handlerType, serviceProvider);
                RegisterRpcHandler(pulseDispatcher, method, handlerType, serviceProvider);
                RegisterOnConnectHandler(pulseDispatcher, method, handlerType, serviceProvider);
                RegisterOnDisconnectHandler(pulseDispatcher, method, handlerType, serviceProvider);

                CollectMetadataEventHandlers(method, handlerType, serviceProvider, metadataHandlerConfigs);
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
        Type handlerType, IServiceProvider? serviceProvider)
    {
        PulseEventAttribute? attribute = method.GetCustomAttribute<PulseEventAttribute>();
        if (attribute == null) return;

        ParameterInfo[] parameters = method.GetParameters();
        if (method.ReturnType != typeof(Task)) return;

        string handle = attribute.Handle ?? method.Name;

        if (parameters.Length == 1 && parameters[0].ParameterType == typeof(PulseContext))
        {
            if (serviceProvider != null)
            {
                async Task ScopedHandler(PulseContext context)
                {
                    if (!context.Connection.TryGetMetadata("__scope", out IServiceScope? scope) || scope == null) 
                        throw new InvalidOperationException("Service scope not found. Ensure scoping middleware is registered.");

                    object handlerInstance = ActivatorUtilities.CreateInstance(scope.ServiceProvider, handlerType);

                    await (Task)method.Invoke(handlerInstance, [context])!;
                }

                pulseDispatcher.MapEvent(handle, ScopedHandler, "v1");
            }
            else
            {
                object handlerInstance = Activator.CreateInstance(handlerType)!;
                Type delegateType = typeof(Func<,>).MakeGenericType(typeof(PulseContext), typeof(Task));
                Delegate handlerDelegate = Delegate.CreateDelegate(delegateType, handlerInstance, method);
                pulseDispatcher.MapEvent(handle, (Func<PulseContext, Task>)handlerDelegate, "v1");
            }
        }
        else if (parameters.Length == 2 && parameters[1].ParameterType == typeof(PulseContext))
        {
            Type payloadType = parameters[0].ParameterType;

            if (serviceProvider != null)
            {
                MethodInfo scopedHandlerMethod = typeof(PulseAttributesExtensions)
                    .GetMethod(nameof(CreateScopedEventHandler), BindingFlags.NonPublic | BindingFlags.Static)!
                    .MakeGenericMethod(payloadType);

                object? scopedHandler = scopedHandlerMethod.Invoke(null, [method, handlerType]);

                MethodInfo mapEventMethod = typeof(PulseDispatcher)
                    .GetMethods()
                    .Single(methodInfo => methodInfo is { Name: nameof(PulseDispatcher.MapEvent), IsGenericMethodDefinition: true }
                                          && methodInfo.GetGenericArguments().Length == 1)
                    .MakeGenericMethod(payloadType);

                mapEventMethod.Invoke(pulseDispatcher, [handle, scopedHandler, "v1"]);
            }
            else
            {
                object handlerInstance = Activator.CreateInstance(handlerType)!;
                Type delegateType = typeof(Func<,,>).MakeGenericType(payloadType, typeof(PulseContext), typeof(Task));
                Delegate handlerDelegate = Delegate.CreateDelegate(delegateType, handlerInstance, method);

                MethodInfo mapEventMethod = typeof(PulseDispatcher)
                    .GetMethods()
                    .Single(methodInfo => methodInfo is { Name: nameof(PulseDispatcher.MapEvent), IsGenericMethodDefinition: true }
                                          && methodInfo.GetGenericArguments().Length == 1)
                    .MakeGenericMethod(payloadType);

                mapEventMethod.Invoke(pulseDispatcher, [handle, handlerDelegate, "v1"]);
            }
        }
    }

    private static Func<T, PulseContext, Task> CreateScopedEventHandler<T>(MethodInfo method, Type handlerType)
    {
        return async (payload, context) =>
        {
            if (!context.Connection.TryGetMetadata("__scope", out IServiceScope? scope) || scope == null)
                throw new InvalidOperationException(
                    "Service scope not found. Ensure scoping middleware is registered.");

            object handlerInstance = ActivatorUtilities.CreateInstance(scope.ServiceProvider, handlerType);

            await (Task)method.Invoke(handlerInstance, [payload, context])!;
        };
    }

    private static void RegisterRpcHandler(PulseDispatcher pulseDispatcher, MethodInfo method,
        Type handlerType, IServiceProvider? serviceProvider)
    {
        PulseRpcAttribute? attribute = method.GetCustomAttribute<PulseRpcAttribute>();
        if (attribute == null) return;

        ParameterInfo[] parameters = method.GetParameters();
        if (!method.ReturnType.IsGenericType || method.ReturnType.GetGenericTypeDefinition() != typeof(Task<>)) return;

        Type responseType = method.ReturnType.GetGenericArguments()[0];
        string handle = attribute.Handle ?? method.Name;

        if (parameters.Length == 1 && parameters[0].ParameterType == typeof(PulseContext))
        {
            if (serviceProvider != null)
            {
                MethodInfo scopedHandlerMethod = typeof(PulseAttributesExtensions)
                    .GetMethod(nameof(CreateScopedRpcHandler), BindingFlags.NonPublic | BindingFlags.Static)!
                    .MakeGenericMethod(responseType);

                object? scopedHandler = scopedHandlerMethod.Invoke(null, [method, handlerType]);

                MethodInfo mapRpcMethod = typeof(PulseDispatcher)
                    .GetMethods()
                    .Single(methodInfo => methodInfo is { Name: nameof(PulseDispatcher.MapRpc), IsGenericMethodDefinition: true }
                                          && methodInfo.GetGenericArguments().Length == 1)
                    .MakeGenericMethod(responseType);

                mapRpcMethod.Invoke(pulseDispatcher, [handle, scopedHandler, "v1"]);
            }
            else
            {
                object handlerInstance = Activator.CreateInstance(handlerType)!;
                Type delegateType = typeof(Func<,>).MakeGenericType(typeof(PulseContext),
                    typeof(Task<>).MakeGenericType(responseType));
                Delegate handlerDelegate = Delegate.CreateDelegate(delegateType, handlerInstance, method);

                MethodInfo mapRpcMethod = typeof(PulseDispatcher)
                    .GetMethods()
                    .Single(methodInfo => methodInfo is { Name: nameof(PulseDispatcher.MapRpc), IsGenericMethodDefinition: true }
                                          && methodInfo.GetGenericArguments().Length == 1)
                    .MakeGenericMethod(responseType);

                mapRpcMethod.Invoke(pulseDispatcher, [handle, handlerDelegate, "v1"]);
            }
        }
        else if (parameters.Length == 2 && parameters[1].ParameterType == typeof(PulseContext))
        {
            Type requestType = parameters[0].ParameterType;

            if (serviceProvider != null)
            {
                MethodInfo scopedHandlerMethod = typeof(PulseAttributesExtensions)
                    .GetMethod(nameof(CreateScopedRpcHandlerWithRequest), BindingFlags.NonPublic | BindingFlags.Static)!
                    .MakeGenericMethod(requestType, responseType);

                object? scopedHandler = scopedHandlerMethod.Invoke(null, [method, handlerType]);

                MethodInfo mapRpcMethod = typeof(PulseDispatcher)
                    .GetMethods()
                    .Single(methodInfo => methodInfo is { Name: nameof(PulseDispatcher.MapRpc), IsGenericMethodDefinition: true }
                                          && methodInfo.GetGenericArguments().Length == 2)
                    .MakeGenericMethod(requestType, responseType);

                mapRpcMethod.Invoke(pulseDispatcher, [handle, scopedHandler, "v1"]);
            }
            else
            {
                object handlerInstance = Activator.CreateInstance(handlerType)!;
                Type delegateType = typeof(Func<,,>).MakeGenericType(requestType, typeof(PulseContext),
                    typeof(Task<>).MakeGenericType(responseType));
                Delegate handlerDelegate = Delegate.CreateDelegate(delegateType, handlerInstance, method);

                MethodInfo mapRpcMethod = typeof(PulseDispatcher)
                    .GetMethods()
                    .Single(methodInfo => methodInfo is { Name: nameof(PulseDispatcher.MapRpc), IsGenericMethodDefinition: true }
                                          && methodInfo.GetGenericArguments().Length == 2)
                    .MakeGenericMethod(requestType, responseType);

                mapRpcMethod.Invoke(pulseDispatcher, [handle, handlerDelegate, "v1"]);
            }
        }
    }

    private static Func<PulseContext, Task<TResponse>> CreateScopedRpcHandler<TResponse>(MethodInfo method,
        Type handlerType)
    {
        return async context =>
        {
            if (!context.Connection.TryGetMetadata("__scope", out IServiceScope? scope) || scope == null)
                throw new InvalidOperationException(
                    "Service scope not found. Ensure scoping middleware is registered.");

            object handlerInstance = ActivatorUtilities.CreateInstance(scope.ServiceProvider, handlerType);

            Task<TResponse> task = (Task<TResponse>)method.Invoke(handlerInstance, [context])!;
            return await task;
        };
    }

    private static Func<TRequest, PulseContext, Task<TResponse>> CreateScopedRpcHandlerWithRequest<TRequest, TResponse>(
        MethodInfo method, Type handlerType)
    {
        return async (request, context) =>
        {
            if (!context.Connection.TryGetMetadata("__scope", out IServiceScope? scope) || scope == null)
                throw new InvalidOperationException(
                    "Service scope not found. Ensure scoping middleware is registered.");

            object handlerInstance = ActivatorUtilities.CreateInstance(scope.ServiceProvider, handlerType);

            Task<TResponse> task = (Task<TResponse>)method.Invoke(handlerInstance, [request, context])!;
            return await task;
        };
    }

    private static void RegisterOnConnectHandler(PulseDispatcher pulseDispatcher, MethodInfo method,
        Type handlerType, IServiceProvider? serviceProvider)
    {
        PulseOnConnectAttribute? attribute = method.GetCustomAttribute<PulseOnConnectAttribute>();
        if (attribute == null) return;

        ParameterInfo[] parameters = method.GetParameters();
        if (method.ReturnType != typeof(Task) || parameters.Length != 1 ||
            parameters[0].ParameterType != typeof(PulseConnection)) return;

        if (serviceProvider != null)
        {
            async Task Handler(PulseConnection connection)
            {
                using IServiceScope scope = serviceProvider.CreateScope();
                object handlerInstance = ActivatorUtilities.CreateInstance(scope.ServiceProvider, handlerType);
                await (Task)method.Invoke(handlerInstance, [connection])!;
            }

            pulseDispatcher.AddOnConnectHandler(Handler);
        }
        else
        {
            object handlerInstance = Activator.CreateInstance(handlerType)!;
            Func<PulseConnection, Task> handler = (Func<PulseConnection, Task>)Delegate.CreateDelegate(
                typeof(Func<PulseConnection, Task>), handlerInstance, method);

            pulseDispatcher.AddOnConnectHandler(handler);
        }
    }

    private static void RegisterOnDisconnectHandler(PulseDispatcher pulseDispatcher, MethodInfo method,
        Type handlerType, IServiceProvider? serviceProvider)
    {
        PulseOnDisconnectAttribute? attribute = method.GetCustomAttribute<PulseOnDisconnectAttribute>();
        if (attribute == null) return;

        ParameterInfo[] parameters = method.GetParameters();
        if (method.ReturnType != typeof(Task) || parameters.Length != 1 ||
            parameters[0].ParameterType != typeof(PulseConnection)) return;

        if (serviceProvider != null)
        {
            async Task Handler(PulseConnection connection)
            {
                using IServiceScope scope = serviceProvider.CreateScope();
                object handlerInstance = ActivatorUtilities.CreateInstance(scope.ServiceProvider, handlerType);
                await (Task)method.Invoke(handlerInstance, [connection])!;
            }

            pulseDispatcher.AddOnDisconnectHandler(Handler);
        }
        else
        {
            object handlerInstance = Activator.CreateInstance(handlerType)!;
            Func<PulseConnection, Task> handler = (Func<PulseConnection, Task>)Delegate.CreateDelegate(
                typeof(Func<PulseConnection, Task>), handlerInstance, method);

            pulseDispatcher.AddOnDisconnectHandler(handler);
        }
    }

    private static void CollectMetadataEventHandlers(MethodInfo method, Type handlerType,
        IServiceProvider? serviceProvider, List<PulseMetadataHandlerConfig> configs)
    {
        IEnumerable<PulseMetadataChangedAttribute> attributes =
            method.GetCustomAttributes<PulseMetadataChangedAttribute>();
        
        List<PulseMetadataChangedAttribute> pulseMetadataChangedAttributes = attributes.ToList();
        if (!pulseMetadataChangedAttributes.Any()) return;

        ParameterInfo[] parameters = method.GetParameters();
        if (method.ReturnType != typeof(Task) || parameters.Length != 2 ||
            parameters[0].ParameterType != typeof(PulseConnection) ||
            parameters[1].ParameterType != typeof(PulseMetadataChangedEventArgs)) return;

        configs.AddRange(pulseMetadataChangedAttributes.Select(attribute => new PulseMetadataHandlerConfig
        {
            Key = attribute.Key,
            ChangeTypes = attribute.ChangeTypes,
            Handler = serviceProvider != null
                ? async (connection, args) =>
                {
                    using IServiceScope scope = serviceProvider.CreateScope();
                    object handlerInstance = ActivatorUtilities.CreateInstance(scope.ServiceProvider, handlerType);
                    await (Task)method.Invoke(handlerInstance, [connection, args])!;
                }
                : async (connection, args) =>
                {
                    object handlerInstance = Activator.CreateInstance(handlerType)!;
                    await (Task)method.Invoke(handlerInstance, [connection, args])!;
                }
        }));
    }
}