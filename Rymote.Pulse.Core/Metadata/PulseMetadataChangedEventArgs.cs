
namespace Rymote.Pulse.Core.Metadata;

public class PulseMetadataChangedEventArgs : EventArgs
{
    public string Key { get; }
    public object? OldValue { get; }
    public object? NewValue { get; }
    public PulseMetadataChangeType ChangeType { get; }
    public DateTime Timestamp { get; }
    public DateTime? CreatedAt { get; }
    public DateTime? PreviousModifiedAt { get; }
    public int ModificationCount { get; }
    
    public PulseMetadataChangedEventArgs(
        string key, 
        object? oldValue, 
        object? newValue, 
        PulseMetadataChangeType changeType,
        DateTime? createdAt = null,
        DateTime? previousModifiedAt = null,
        int modificationCount = 0)
    {
        Key = key;
        OldValue = oldValue;
        NewValue = newValue;
        ChangeType = changeType;
        Timestamp = DateTime.UtcNow;
        CreatedAt = createdAt;
        PreviousModifiedAt = previousModifiedAt;
        ModificationCount = modificationCount;
    }
}