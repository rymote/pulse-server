using System.Collections.Concurrent;
using System.Collections.Immutable;

namespace Rymote.Pulse.Core.Metadata;

public class PulseMetadata : IDisposable
{
    private const int MAX_METADATA_ENTRIES = 100;
    private readonly ConcurrentDictionary<string, PulseMetadataEntry> _entries = new();

    private readonly ConcurrentDictionary<string, ImmutableList<PulseMetadataChangedEventHandler>> _keyHandlers = new();

    private volatile ImmutableList<PulseMetadataChangedEventHandler> _globalHandlers =
        ImmutableList<PulseMetadataChangedEventHandler>.Empty;

    private bool _disposed;

    public IReadOnlyDictionary<string, object> Data =>
        new Dictionary<string, object>(_entries.ToDictionary(keyValuePair => keyValuePair.Key,
            keyValuePair => keyValuePair.Value.Value));

    public IReadOnlyDictionary<string, PulseMetadataEntry> Entries =>
        new Dictionary<string, PulseMetadataEntry>(_entries);

    public IEnumerable<string> Keys => _entries.Keys;

    public int Count => _entries.Count;

    public void Subscribe(string key, PulseMetadataChangedEventHandler handler)
    {
        ThrowIfDisposed();

        _keyHandlers.AddOrUpdate(key,
            ImmutableList.Create(handler),
            (_, existing) => existing.Add(handler));
    }

    public void SubscribeGlobal(PulseMetadataChangedEventHandler handler)
    {
        ThrowIfDisposed();

        ImmutableList<PulseMetadataChangedEventHandler> current, updated;

        do
        {
            current = _globalHandlers;
            updated = current.Add(handler);
        } while (Interlocked.CompareExchange(ref _globalHandlers, updated, current) != current);
    }

    public void Unsubscribe(string key, PulseMetadataChangedEventHandler handler)
    {
        _keyHandlers.AddOrUpdate(key,
            ImmutableList<PulseMetadataChangedEventHandler>.Empty,
            (_, existing) =>
            {
                ImmutableList<PulseMetadataChangedEventHandler> updated = existing.Remove(handler);
                return updated.IsEmpty ? ImmutableList<PulseMetadataChangedEventHandler>.Empty : updated;
            });

        if (_keyHandlers.TryGetValue(key, out ImmutableList<PulseMetadataChangedEventHandler>? handlers) &&
            handlers.IsEmpty)
            _keyHandlers.TryRemove(key, out _);
    }

    public void UnsubscribeGlobal(PulseMetadataChangedEventHandler handler)
    {
        ImmutableList<PulseMetadataChangedEventHandler> current, updated;
        
        do
        {
            current = _globalHandlers;
            updated = current.Remove(handler);
        } 
        while (Interlocked.CompareExchange(ref _globalHandlers, updated, current) != current);
    }

    private void ClearAllHandlers()
    {
        _keyHandlers.Clear();
        _globalHandlers = ImmutableList<PulseMetadataChangedEventHandler>.Empty;
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

        ImmutableList<PulseMetadataChangedEventHandler> keyHandlers = _keyHandlers.TryGetValue(key, out ImmutableList<PulseMetadataChangedEventHandler>? handlers) ? handlers : ImmutableList<PulseMetadataChangedEventHandler>.Empty;
        ImmutableList<PulseMetadataChangedEventHandler> globalHandlers = _globalHandlers;

        int totalHandlers = keyHandlers.Count + globalHandlers.Count;
        if (totalHandlers == 0) return;

        Task[] tasks = new Task[totalHandlers];
        int taskIndex = 0;

        foreach (PulseMetadataChangedEventHandler handler in keyHandlers)
            tasks[taskIndex++] = ExecuteHandlerSafely(handler, this, arguments);
        
        foreach (PulseMetadataChangedEventHandler handler in globalHandlers)
            tasks[taskIndex++] = ExecuteHandlerSafely(handler, this, arguments);
        
        await Task.WhenAll(tasks).ConfigureAwait(false);
    }

    private static async Task ExecuteHandlerSafely(PulseMetadataChangedEventHandler handler, 
        PulseMetadata metadata, PulseMetadataChangedEventArgs args)
    {
        try
        {
            await handler(metadata, args).ConfigureAwait(false);
        }
        catch (Exception)
        {
            // Isolate handler errors - could add logging here if needed
            // Prevents one bad handler from affecting others
        }
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