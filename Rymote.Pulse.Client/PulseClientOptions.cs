namespace Rymote.Pulse.Client;

public class PulseClientOptions
{
    public required Uri Endpoint { get; set; }
    public required IList<IPulseClientTransport> Transports { get; set; }
    public string? AuthToken { get; set; }
    public IReadOnlyDictionary<string, string>? QueryParameters { get; set; }
    public bool AutoReconnect { get; set; } = true;
    public TimeSpan ReconnectInitialDelay { get; set; } = TimeSpan.FromSeconds(1);
    public TimeSpan ReconnectMaxDelay { get; set; } = TimeSpan.FromSeconds(30);
    public TimeSpan ConnectTimeout { get; set; } = TimeSpan.FromSeconds(5);
    public TimeSpan RequestTimeout { get; set; } = TimeSpan.FromSeconds(15);
}
