using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Rymote.Pulse.Core;

namespace Rymote.Pulse.Attributes;

public static class PulseAttributesExtensions
{
    public static void RegisterHandlersFromAssembly(this PulseDispatcher pulseDispatcher, Assembly assembly, IServiceProvider? serviceProvider = null)
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

    private static void RegisterEventHandler(PulseDispatcher pulseDispatcher, MethodInfo method, object? handlerInstance)
    {
        if (handlerInstance == null) return;
        
        PulseEventAttribute? attribute = method.GetCustomAttribute<PulseEventAttribute>();
        if (attribute == null) return;
        
        var parameters = method.GetParameters();
        if (parameters.Length != 2) return;
        
        Type payloadType = parameters[0].ParameterType;
        Type contextType =  parameters[1].ParameterType;

        if (contextType != typeof(PulseContext) || method.ReturnType != typeof(Task)) return;

        string handle = attribute.Handle ?? method.Name;
        
        MethodInfo mapEventMethod = typeof(PulseDispatcher)
            .GetMethod(nameof(PulseDispatcher.MapEvent))!
            .MakeGenericMethod(payloadType);
        
        Type delegateType = typeof(Func<,,>).MakeGenericType(payloadType, typeof(PulseContext), typeof(Task));
        Delegate handlerDelegate = Delegate.CreateDelegate(delegateType, handlerInstance, method);

        mapEventMethod.Invoke(pulseDispatcher, new object[] { handle, handlerDelegate, "v1" });
    }
    
    private static void RegisterRpcHandler(PulseDispatcher pulseDispatcher, MethodInfo method, object? handlerInstance)
    {
        if (handlerInstance == null) return;
        
        PulseRpcAttribute? attribute = method.GetCustomAttribute<PulseRpcAttribute>();
        if (attribute == null) return;
        
        var parameters = method.GetParameters();
        if (parameters.Length != 2) return;
        
        Type requestType = parameters[0].ParameterType;
        Type contextType =  parameters[1].ParameterType;

        if (contextType != typeof(PulseContext) || method.ReturnType.GetGenericTypeDefinition() != typeof(Task<>)) return;

        Type responseType = method.ReturnType.GetGenericArguments()[0];
        string handle = attribute.Handle ?? method.Name;
        
        MethodInfo mapRpcMethod = typeof(PulseDispatcher)
            .GetMethod(nameof(PulseDispatcher.MapRpc))!
            .MakeGenericMethod(requestType, responseType);
        
        Type delegateType = typeof(Func<,,,>).MakeGenericType(requestType, typeof(PulseContext), typeof(Task<>).MakeGenericType(responseType));
        Delegate handlerDelegate = Delegate.CreateDelegate(delegateType, handlerInstance, method);

        mapRpcMethod.Invoke(pulseDispatcher, new object[] { handle, handlerDelegate, "v1" });
    }
}