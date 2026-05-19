using System.Collections.Concurrent;
using Rymote.Pulse.Core.Logging;

namespace Rymote.Pulse.Tests.Helpers;

internal sealed class TestLogger : IPulseLogger
{
    public ConcurrentQueue<string> DebugMessages { get; } = new();
    public ConcurrentQueue<string> InfoMessages { get; } = new();
    public ConcurrentQueue<string> WarningMessages { get; } = new();
    public ConcurrentQueue<(string Message, Exception? Exception)> ErrorMessages { get; } = new();

    public void LogDebug(string message) => DebugMessages.Enqueue(message);
    public void LogInfo(string message) => InfoMessages.Enqueue(message);
    public void LogWarning(string message) => WarningMessages.Enqueue(message);
    public void LogError(string message, Exception? exception = null)
        => ErrorMessages.Enqueue((message, exception));
}
