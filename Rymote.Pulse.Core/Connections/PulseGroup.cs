using System.Collections.Concurrent;
using System.Collections.Immutable;
using Rymote.Pulse.Core.Messages;
using Rymote.Pulse.Core.Serialization;

namespace Rymote.Pulse.Core.Connections;

public class PulseGroup : IDisposable
{
    private readonly ConcurrentDictionary<string, PulseConnection> _members = new();
    
    private volatile ImmutableList<PulseConnection>? _cachedActiveMembers;
    private volatile bool _membersCacheInvalid = true;
    private readonly ReaderWriterLockSlim _cacheLock = new();
    
    private readonly ConcurrentDictionary<string, byte> _failedConnections = new();

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
        _failedConnections.TryRemove(connection.ConnectionId, out _);
        
        InvalidateCache();
        UpdateLastActivity();
        return true;
    }

    public bool Remove(string connectionId)
    {
        bool removed = _members.TryRemove(connectionId, out _);
        _failedConnections.TryRemove(connectionId, out _);

        if (removed)
        {
            InvalidateCache();
            UpdateLastActivity();
        }

        return removed;
    }

    public void Remove(PulseConnection connection) => Remove(connection.ConnectionId);

    public IEnumerable<PulseConnection> Members
    {
        get
        {
            _cacheLock.EnterReadLock();
            try
            {
                if (!_membersCacheInvalid && _cachedActiveMembers != null)
                    return _cachedActiveMembers;
            }
            finally
            {
                _cacheLock.ExitReadLock();
            }

            return GetActiveMembers();
        }
    }

    private ImmutableList<PulseConnection> GetActiveMembers()
    {
        _cacheLock.EnterWriteLock();
        try
        {
            if (!_membersCacheInvalid && _cachedActiveMembers != null)
                return _cachedActiveMembers;

            var activeMembers = ImmutableList.CreateBuilder<PulseConnection>();
            var toRemove = new List<string>();

            foreach (var keyValuePair in _members)
            {
                if (_failedConnections.ContainsKey(keyValuePair.Key))
                {
                    toRemove.Add(keyValuePair.Key);
                    continue;
                }

                if (keyValuePair.Value.IsOpen)
                    activeMembers.Add(keyValuePair.Value);
                else
                {
                    toRemove.Add(keyValuePair.Key);
                    _failedConnections.TryAdd(keyValuePair.Key, 0);
                }
            }

            foreach (string connectionId in toRemove)
                _members.TryRemove(connectionId, out _);

            _cachedActiveMembers = activeMembers.ToImmutable();
            _membersCacheInvalid = false;
            
            return _cachedActiveMembers;
        }
        finally
        {
            _cacheLock.ExitWriteLock();
        }
    }
    
    private void InvalidateCache()
    {
        _cacheLock.EnterWriteLock();
        
        try
        {
            _membersCacheInvalid = true;
            _cachedActiveMembers = null;
        }
        finally
        {
            _cacheLock.ExitWriteLock();
        }
    }
    
    public bool Contains(string connectionId) => _members.ContainsKey(connectionId);

    public bool Contains(PulseConnection connection) => Contains(connection.ConnectionId);

    public async Task SendEventAsync<TPayload>(
        string handle,
        TPayload data,
        string version = "v1",
        CancellationToken cancellationToken = default
    ) 
    {
        PulseEnvelope<TPayload> envelope = new PulseEnvelope<TPayload>
        {
            Handle = handle,
            Body = data,
            Kind = PulseKind.EVENT,
            Version = version
        };

        byte[] envelopeBytes = MsgPackSerdes.Serialize(envelope);
        await BroadcastAsync(envelopeBytes, cancellationToken);
    }

    public async Task SendEventAsync(
        string handle,
        object data,
        string version = "v1",
        CancellationToken cancellationToken = default
    )
    {
        PulseEnvelope<object> envelope = new PulseEnvelope<object>
        {
            Handle = handle,
            Body = data,
            Kind = PulseKind.EVENT,
            Version = version
        };

        byte[] envelopeBytes = MsgPackSerdes.Serialize(envelope);
        await BroadcastAsync(envelopeBytes, cancellationToken);
    }
    
    public async Task BroadcastAsync(byte[] payload, CancellationToken cancellationToken = default)
    {
        ThrowIfDisposed();
        UpdateLastActivity();

        ImmutableList<PulseConnection> activeConnections = GetActiveMembers();

        if (activeConnections.Count == 0)
            return;

        ConcurrentBag<string> failedConnections = new ConcurrentBag<string>();
        
        Task[] tasks = new Task[activeConnections.Count];
        for (int i = 0; i < activeConnections.Count; i++)
        {
            PulseConnection connection = activeConnections[i];
            tasks[i] = SendToConnectionAsync(connection, payload, failedConnections, cancellationToken);
        }

        await Task.WhenAll(tasks).ConfigureAwait(false);

        if (!failedConnections.IsEmpty)
        {
            foreach (string connectionId in failedConnections)
            {
                _members.TryRemove(connectionId, out _);
                _failedConnections.TryAdd(connectionId, 0);
            }
            InvalidateCache();
        }
    }

    public int CleanupClosedConnections()
    {
        List<string> toRemove = _failedConnections.Keys.Where(failedId => _members.TryRemove(failedId, out _)).ToList();

        _failedConnections.Clear();
        
        foreach (KeyValuePair<string, PulseConnection> keyValuePair in _members)
        {
            if (keyValuePair.Value.IsOpen) continue;
            
            toRemove.Add(keyValuePair.Key);
            _failedConnections.TryAdd(keyValuePair.Key, 0);
        }

        foreach (string connectionId in toRemove)
            _members.TryRemove(connectionId, out _);

        if (toRemove.Count <= 0) 
            return toRemove.Count;
        
        InvalidateCache();
        UpdateLastActivity();

        return toRemove.Count;
    }

    private async Task SendToConnectionAsync(PulseConnection connection, byte[] payload,
        ConcurrentBag<string> failedConnections, CancellationToken cancellationToken)
    {
        try
        {
            if (connection.IsOpen)
                await connection.SendAsync(payload, cancellationToken).ConfigureAwait(false);
            else
                failedConnections.Add(connection.ConnectionId);
        }
        catch (Exception)
        {
            failedConnections.Add(connection.ConnectionId);
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
        _cacheLock?.Dispose();
        _members.Clear();
        _failedConnections.Clear();
        GC.SuppressFinalize(this);
    }
}