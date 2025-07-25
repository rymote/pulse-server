namespace Rymote.Pulse.Core.Metadata;

public class PulseMetadataEntry
{
    public string Key { get; }
    public object Value { get; set; }
    public DateTime CreatedAt { get; }
    public DateTime ModifiedAt { get; set; }
    public int ModificationCount { get; set; }

    public PulseMetadataEntry(string key, object value)
    {
        Key = key;
        Value = value;
        CreatedAt = DateTime.UtcNow;
        ModifiedAt = DateTime.UtcNow;
        ModificationCount = 0;
    }

    public void UpdateValue(object newValue)
    {
        Value = newValue;
        ModifiedAt = DateTime.UtcNow;
        ModificationCount++;
    }

    public TimeSpan Age => DateTime.UtcNow - CreatedAt;

    public TimeSpan TimeSinceLastModification => DateTime.UtcNow - ModifiedAt;

    public bool WasModified => ModificationCount > 0;
}