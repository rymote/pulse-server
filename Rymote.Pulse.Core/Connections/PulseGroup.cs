using System.Collections.Concurrent;

namespace Rymote.Pulse.Core.Connections;

public class PulseGroup : IDisposable
{
    private readonly ConcurrentDictionary<string, PulseConnection> _members
        = new ConcurrentDictionary<string, PulseConnection>();

    private readonly Timer? _cleanupTimer;
    private readonly object _lockObject = new();
    private bool _disposed;

    public string Name { get; }
    public DateTime CreatedAt { get; }
    public DateTime LastActivity { get; private set; }

    public PulseGroup(string name, bool enablePeriodicCleanup = true)
    {
        Name = name;
        CreatedAt = DateTime.UtcNow;
        LastActivity = DateTime.UtcNow;

        if (enablePeriodicCleanup)
            _cleanupTimer = new Timer(PeriodicCleanup, null,
                TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
    }

    public bool Add(PulseConnection connection)
    {
        ThrowIfDisposed();

        if (!connection.IsOpen) return false;

        _members[connection.ConnectionId] = connection;
        UpdateLastActivity();
        return true;
    }

    public bool Remove(string connectionId)
    {
        bool removed = _members.TryRemove(connectionId, out _);
        if (removed)
            UpdateLastActivity();

        return removed;
    }

    public void Remove(PulseConnection connection) => Remove(connection.ConnectionId);

    public IEnumerable<PulseConnection> Members
    {
        get
        {
            List<PulseConnection> activeMembers = [];
            List<string> toRemove = [];

            foreach (KeyValuePair<string, PulseConnection> keyValuePair in _members)
                if (keyValuePair.Value.IsOpen)
                    activeMembers.Add(keyValuePair.Value);
                else
                    toRemove.Add(keyValuePair.Key);

            foreach (string connectionId in toRemove)
                _members.TryRemove(connectionId, out _);

            return activeMembers;
        }
    }

    public bool Contains(string connectionId) => _members.ContainsKey(connectionId);

    public bool Contains(PulseConnection connection) => Contains(connection.ConnectionId);

    public async Task BroadcastAsync(byte[] payload, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        UpdateLastActivity();

        List<PulseConnection> activeConnections = Members.ToList();

        if (activeConnections.Count == 0)
            return;

        List<string> failedConnections = new List<string>();
        List<Task> tasks = activeConnections.Select(connection =>
            SendToConnectionAsync(connection, payload, failedConnections, cancellationToken)).ToList();

        await Task.WhenAll(tasks);

        foreach (string connectionId in failedConnections)
            _members.TryRemove(connectionId, out _);
    }

    public int CleanupClosedConnections()
    {
        List<string> closedConnections = _members
            .Where(keyValuePair => !keyValuePair.Value.IsOpen)
            .Select(keyValuePair => keyValuePair.Key)
            .ToList();

        foreach (string connectionId in closedConnections)
            _members.TryRemove(connectionId, out _);

        if (closedConnections.Count != 0)
            UpdateLastActivity();

        return closedConnections.Count;
    }

    private async Task SendToConnectionAsync(PulseConnection connection, byte[] payload,
        List<string> failedConnections, CancellationToken cancellationToken)
    {
        try
        {
            if (connection.IsOpen)
            {
                await connection.SendAsync(payload, cancellationToken);
            }
            else
            {
                lock (failedConnections)
                {
                    failedConnections.Add(connection.ConnectionId);
                }
            }
        }
        catch (Exception)
        {
            // Connection failed, mark for removal
            lock (failedConnections)
            {
                failedConnections.Add(connection.ConnectionId);
            }
        }
    }

    private void PeriodicCleanup(object? state)
    {
        if (_disposed) return;

        try
        {
            CleanupClosedConnections();
        }
        catch (Exception)
        {
        }
    }

    private void UpdateLastActivity()
    {
        LastActivity = DateTime.UtcNow;
    }

    private void ThrowIfDisposed()
    {
        if (!_disposed) return;

        throw new ObjectDisposedException(nameof(PulseGroup));
    }

    public void Dispose()
    {
        if (_disposed) return;

        _disposed = true;
        _cleanupTimer?.Dispose();
        _members.Clear();
        GC.SuppressFinalize(this);
    }
}