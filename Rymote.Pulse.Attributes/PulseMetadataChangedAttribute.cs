namespace Rymote.Pulse.Attributes;

[Flags]
public enum MetadataChangeTypes
{
    CREATED = 1,
    MODIFIED = 2,
    DELETED = 4,
    ALL = CREATED | MODIFIED | DELETED
}

[AttributeUsage(AttributeTargets.Method, AllowMultiple = true)]
public class PulseMetadataChangedAttribute : Attribute
{
    public string? Key { get; }

    public MetadataChangeTypes ChangeTypes { get; }

    public PulseMetadataChangedAttribute(string? key = null, MetadataChangeTypes changeTypes = MetadataChangeTypes.ALL)
    {
        Key = key;
        ChangeTypes = changeTypes;
    }
}