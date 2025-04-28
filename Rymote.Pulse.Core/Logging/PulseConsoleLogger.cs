using System;

namespace Rymote.Pulse.Core.Logging;

public class PulseConsoleLogger : IPulseLogger
{
    public void LogInfo(string message) => Console.WriteLine($"[INFO] {message}");
    
    public void LogWarning(string message) => Console.WriteLine($"[WARN] {message}");
    
    public void LogError(string message, Exception? exception = null)
    {
        Console.WriteLine($"[ERROR] {message}");
        
        if (exception != null)
            Console.WriteLine(exception);
    }
}