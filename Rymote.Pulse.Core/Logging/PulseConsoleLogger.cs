using System;

namespace Rymote.Pulse.Core.Logging;

public class PulseConsoleLogger : IPulseLogger
{
    private readonly bool _enableDebugLogs;

    public PulseConsoleLogger(bool enableDebugLogs = false)
    {
        _enableDebugLogs = enableDebugLogs;
    }

    public void LogDebug(string message)
    {
        if (_enableDebugLogs)
            Console.WriteLine($"[DEBUG] {message}");
    }

    public void LogInfo(string message) => Console.WriteLine($"[INFO] {message}");
    
    public void LogWarning(string message) => Console.WriteLine($"[WARN] {message}");
    
    public void LogError(string message, Exception? exception = null)
    {
        Console.WriteLine($"[ERROR] {message}");
        
        if (exception != null)
            Console.WriteLine(exception);
    }
}