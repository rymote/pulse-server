namespace Rymote.Pulse.Core.Middleware;

public class PulseMiddlewarePipeline
{
    private readonly List<PulseMiddlewareDelegate> _middlewares = new();

    public void Use(PulseMiddlewareDelegate middleware) => _middlewares.Add(middleware);

    public async Task ExecuteAsync(PulseContext context, Func<PulseContext, Task> finalHandler)
    {
        int index = 0;
        
        Task Next()
        {
            if (index >= _middlewares.Count) 
                return finalHandler(context);
            
            PulseMiddlewareDelegate currentMiddleware = _middlewares[index++];
            return currentMiddleware(context, Next);
        }
        
        await Next();
    }
}