using Rymote.Pulse.Core.Exceptions;

namespace Rymote.Pulse.Core.Middleware;

public class ConcurrencyMiddleware
{
    private readonly SemaphoreSlim _semaphore;
    private readonly TimeSpan _waitTimeout;

    public ConcurrencyMiddleware(int maxConcurrentRequests, TimeSpan waitTimeout)
    {
        _semaphore = new SemaphoreSlim(maxConcurrentRequests, maxConcurrentRequests);
        _waitTimeout = waitTimeout;
    }

    public async Task InvokeAsync(PulseContext context, Func<Task> next)
    {
        bool acquired = false;
        
        try
        {
            acquired = await _semaphore.WaitAsync(_waitTimeout);
            if (!acquired)
                throw new PulseException(PulseStatus.TIMEOUT, "Server busy – concurrency limit reached.");
            
            await next();
        }
        finally
        {
            if (acquired)
                _semaphore.Release();
        }
    }
}