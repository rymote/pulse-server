using System.Threading.Tasks;
using Rymote.Pulse.Core.Middleware;

namespace Rymote.Pulse.Core.Streaming;

public interface IStreamHandler<TRequest, TChunk>
{
    Task HandleStreamAsync(TRequest request, PulseContext context, PulseStream<TChunk> stream);
}