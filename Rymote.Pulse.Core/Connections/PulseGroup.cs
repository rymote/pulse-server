using System.Collections.Concurrent;

namespace Rymote.Pulse.Core.Connections;

public class PulseGroup
{
    private readonly ConcurrentDictionary<string, PulseConnection> _members
        = new ConcurrentDictionary<string, PulseConnection>();

    public string Name { get; }
    public bool IsEmpty => _members.IsEmpty;

    public PulseGroup(string name) => Name = name;

    public void Add(PulseConnection connection) => _members[connection.ConnectionId] = connection;
    public void Remove(string connectionId) => _members.TryRemove(connectionId, out _);
    public void Remove(PulseConnection connection) => Remove(connection.ConnectionId);
    
    public IEnumerable<PulseConnection> Members => _members.Values;

    public Task BroadcastAsync(byte[] payload, CancellationToken cancellationToken = default)
    {
        IEnumerable<Task> tasks = Members
            .Where(connection => connection.IsOpen)
            .Select(connection => connection.SendAsync(payload, cancellationToken));
        
        return Task.WhenAll(tasks);
    }
}