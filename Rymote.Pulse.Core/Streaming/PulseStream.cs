using System.Collections.Generic;
using System.Threading.Channels;
using System.Threading.Tasks;

namespace Rymote.Pulse.Core.Streaming;

public class PulseStream<TChunk>
{
    private readonly Channel<TChunk> _channel;
    
    public PulseStream() => _channel = Channel.CreateUnbounded<TChunk>();

    public ValueTask WriteChunkAsync(TChunk chunk) => _channel.Writer.WriteAsync(chunk);
    
    public void Complete() => _channel.Writer.Complete();
    
    public IAsyncEnumerable<TChunk> ReadAllAsync() => _channel.Reader.ReadAllAsync();
}