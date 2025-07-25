using System.Collections.Concurrent;

namespace Rymote.Pulse.Core.Metadata;

public class PulseMetadata : IDisposable
{
    private const int MAX_METADATA_ENTRIES = 100;
    private readonly ConcurrentDictionary<string, PulseMetadataEntry> _entries = new();
    private readonly ConcurrentDictionary<string, List<PulseMetadataChangedEventHandler>> _keyHandlers = new();
    private readonly List<PulseMetadataChangedEventHandler> _globalHandlers = [];
    private readonly Lock _handlerLock = new();
    private bool _disposed;

    public IReadOnlyDictionary<string, object> Data => 
        new Dictionary<string, object>(_entries.ToDictionary(keyValuePair => keyValuePair.Key, keyValuePair => keyValuePair.Value.Value));

    public IReadOnlyDictionary<string, PulseMetadataEntry> Entries => 
        new Dictionary<string, PulseMetadataEntry>(_entries);

    public IEnumerable<string> Keys => _entries.Keys;

    public int Count => _entries.Count;

    public void Subscribe(string key, PulseMetadataChangedEventHandler handler)
    {
        ThrowIfDisposed();
        lock (_handlerLock)
        {
            List<PulseMetadataChangedEventHandler> handlers = _keyHandlers.GetOrAdd(key, _ => []);
            handlers.Add(handler);
        }
    }

    public void SubscribeGlobal(PulseMetadataChangedEventHandler handler)
    {
        ThrowIfDisposed();
        lock (_handlerLock)
            _globalHandlers.Add(handler);
    }

    public void Unsubscribe(string key, PulseMetadataChangedEventHandler handler)
    {
        lock (_handlerLock)
        {
            if (!_keyHandlers.TryGetValue(key, out List<PulseMetadataChangedEventHandler>? handlers)) return;
            
            handlers.Remove(handler);
            if (handlers.Count == 0)
                _keyHandlers.TryRemove(key, out _);
        }
    }

    public void UnsubscribeGlobal(PulseMetadataChangedEventHandler handler)
    {
        lock (_handlerLock)
            _globalHandlers.Remove(handler);
    }

    private void ClearAllHandlers()
    {
        lock (_handlerLock)
        {
            _keyHandlers.Clear();
            _globalHandlers.Clear();
        }
    }
    
    public async Task SetAsync(string key, object value)
    {
        ThrowIfDisposed();
        
        if (_entries.Count >= MAX_METADATA_ENTRIES && !_entries.ContainsKey(key))
            throw new InvalidOperationException($"Maximum metadata entries ({MAX_METADATA_ENTRIES}) exceeded");

        PulseMetadataChangeType changeType;
        object? oldValue = null;
        DateTime? createdAt = null;
        DateTime? previousModifiedAt = null;
        int modificationCount = 0;

        if (_entries.TryGetValue(key, out PulseMetadataEntry? existingEntry))
        {
            changeType = PulseMetadataChangeType.MODIFIED;
            oldValue = existingEntry.Value;
            createdAt = existingEntry.CreatedAt;
            previousModifiedAt = existingEntry.ModifiedAt;
            modificationCount = existingEntry.ModificationCount;
            
            existingEntry.UpdateValue(value);
        }
        else
        {
            changeType = PulseMetadataChangeType.CREATED;
            PulseMetadataEntry newEntry = new PulseMetadataEntry(key, value);
            _entries[key] = newEntry;
            createdAt = newEntry.CreatedAt;
        }

        await NotifyHandlersAsync(key, oldValue, value, changeType, createdAt, previousModifiedAt, modificationCount);
    }

    public async Task<bool> RemoveAsync(string key)
    {
        ThrowIfDisposed();
        
        if (!_entries.TryRemove(key, out PulseMetadataEntry? entry)) return false;
        
        await NotifyHandlersAsync(key, entry.Value, null, PulseMetadataChangeType.DELETED, 
            entry.CreatedAt, entry.ModifiedAt, entry.ModificationCount);
        
        return true;
    }

    public bool TryGet<T>(string key, out T? value)
    {
        ThrowIfDisposed();
        
        if (_entries.TryGetValue(key, out PulseMetadataEntry? entry) && entry.Value is T cast)
        {
            value = cast;
            return true;
        }
        
        value = default;
        return false;
    }
    
    public bool TryGetEntry(string key, out PulseMetadataEntry? entry)
    {
        ThrowIfDisposed();
        
        return _entries.TryGetValue(key, out entry);
    }

    public bool ContainsKey(string key)
    {
        ThrowIfDisposed();
        
        return _entries.ContainsKey(key);
    }

    private async Task NotifyHandlersAsync(string key, object? oldValue, object? newValue, 
        PulseMetadataChangeType changeType, DateTime? createdAt = null, 
        DateTime? previousModifiedAt = null, int modificationCount = 0)
    {
        if (_disposed) return;
        
        PulseMetadataChangedEventArgs arguments = new PulseMetadataChangedEventArgs(
            key, oldValue, newValue, changeType, createdAt, previousModifiedAt, modificationCount);
        List<Task> tasks = [];

        if (_keyHandlers.TryGetValue(key, out List<PulseMetadataChangedEventHandler>? keyHandlers))
        {
            List<PulseMetadataChangedEventHandler> handlersCopy;
            lock (_handlerLock)
                handlersCopy = keyHandlers.ToList();

            tasks.AddRange(handlersCopy.Select(handler => handler(this, arguments)));
        }

        List<PulseMetadataChangedEventHandler> globalHandlersCopy;
        lock (_handlerLock)
            globalHandlersCopy = _globalHandlers.ToList();

        tasks.AddRange(globalHandlersCopy.Select(handler => handler(this, arguments)));

        await Task.WhenAll(tasks);
    }
    
    private void ThrowIfDisposed()
    {
        if (!_disposed) return;
        throw new ObjectDisposedException(nameof(PulseMetadata));
    }
    
    public void Dispose()
    {
        if (_disposed) return;
        
        _disposed = true;
        ClearAllHandlers();
        _entries.Clear();
    }
}