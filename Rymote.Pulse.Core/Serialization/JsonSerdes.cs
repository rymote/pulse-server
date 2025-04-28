using System.Text;
using System.Text.Json;
using Rymote.Pulse.Core.Messages;

namespace Rymote.Pulse.Core.Serialization;

public static class JsonSerdes
{
    private static readonly JsonSerializerOptions Options = new JsonSerializerOptions
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        WriteIndented = false
    };

    public static T DeserializeRequest<T>(string json) where T : PulseMessage, new()
    {
        return JsonSerializer.Deserialize<T>(json, Options) ?? new T();
    }

    public static string SerializeResponse<T>(T response) where T : PulseMessage, new()
    {
        return JsonSerializer.Serialize(response, Options);
    }
}