using System.Threading.Tasks;

namespace Rymote.Pulse.Core.Cluster;

public interface IClusterStore
{
    public Task AddConnectionAsync(string sessionId, string nodeId);
    public Task RemoveConnectionAsync(string sessionId);
}