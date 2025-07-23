using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Rymote.Pulse.Core;

namespace Rymote.Pulse.Attributes;

public static class PulseAttributesExtensions
{
    public static void RegisterHandlersFromAssembly(this PulseDispatcher pulseDispatcher, Assembly assembly,
        IServiceProvider? serviceProvider = null)
    {
        List<Type> handlerTypes = assembly.GetTypes()
            .Where(type => type is { IsClass: true, IsAbstract: false })
            .Where(type => type.GetMethods().Any(method =>
                method.GetCustomAttribute<PulseEventAttribute>() != null ||
                method.GetCustomAttribute<PulseRpcAttribute>() != null ||
                method.GetCustomAttribute<PulseStreamAttribute>() != null))
            .ToList();

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
                // RegisterStreamHandler(pulseDispatcher, method, handlerInstance);
            }
        }
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
            
            Type delegateType = typeof(Func<,>).MakeGenericType(typeof(PulseContext), typeof(Task<>).MakeGenericType(responseType));
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
            
            Type delegateType = typeof(Func<,,>).MakeGenericType(requestType, typeof(PulseContext), typeof(Task<>).MakeGenericType(responseType));
            Delegate handlerDelegate = Delegate.CreateDelegate(delegateType, handlerInstance, method);

            mapRpcMethod.Invoke(pulseDispatcher, new object[] { handle, handlerDelegate, "v1" });
        }
    }
}