using Rymote.Pulse.Core.Connections;
using Rymote.Pulse.Core.Logging;
using Rymote.Pulse.Core.Messages;
using Rymote.Pulse.Core.Serialization;

namespace Rymote.Pulse.Core;

public class PulseContext
{
    public PulseConnectionManager ConnectionManager { get; }
    public PulseConnection Connection { get; }
    public IPulseLogger Logger { get; }
    public byte[] RawRequestBytes { get; }
    public PulseEnvelope<object> UntypedRequest { get; }
    public Dictionary<string, string> Parameters { get; internal set; }
    public CancellationToken CancellationToken { get; }

    public PulseContext(
        PulseConnectionManager connectionManager,
        PulseConnection connection,
        byte[] rawBytes,
        IPulseLogger logger,
        Dictionary<string, string> parameters,
        CancellationToken cancellationToken)
    {
        ConnectionManager = connectionManager;
        Connection = connection;
        Logger = logger;
        RawRequestBytes = rawBytes;
        UntypedRequest = MsgPackSerdes.Deserialize<PulseEnvelope<object>>(rawBytes);
        Parameters = parameters;
        CancellationToken = cancellationToken;
    }

    public PulseEnvelope<TRequest> GetTypedRequestEnvelope<TRequest>()
    {
        return MsgPackSerdes.Deserialize<PulseEnvelope<TRequest>>(RawRequestBytes);
    }

    public TParameter? GetParameter<TParameter>(string name)
    {
        if (!Parameters.TryGetValue(name, out string? value)) return default(TParameter);

        try
        {
            return (TParameter)Convert.ChangeType(value, typeof(TParameter));
        }
        catch (InvalidCastException)
        {
            return default(TParameter);
        }
    }

    public TParameter GetRequiredParameter<TParameter>(string name)
    {
        if (!Parameters.TryGetValue(name, out string? value))
            throw new KeyNotFoundException($"Required parameter '{name}' not found.");

        try
        {
            return (TParameter)Convert.ChangeType(value, typeof(TParameter));
        }
        catch (InvalidCastException invalidCastException)
        {
            throw new InvalidOperationException(
                $"Cannot convert parameter '{name}' to type {typeof(TParameter).Name}.",
                invalidCastException);
        }
    }

    public Task SendEventAsync<TPayload>(
        string handle,
        TPayload data,
        string version = "v1",
        PulseDeliveryMode deliveryMode = PulseDeliveryMode.Reliable,
        CancellationToken cancellationToken = default)
    {
        return Connection.SendEventAsync(handle, data, version, deliveryMode, cancellationToken);
    }

    public Task SendEventAsync<TPayload>(
        PulseConnection connection,
        string handle,
        TPayload data,
        string version = "v1",
        PulseDeliveryMode deliveryMode = PulseDeliveryMode.Reliable,
        CancellationToken cancellationToken = default)
    {
        return connection.SendEventAsync(handle, data, version, deliveryMode, cancellationToken);
    }

    public Task SendEventAsync<TPayload>(
        PulseGroup group,
        string handle,
        TPayload data,
        string version = "v1",
        PulseDeliveryMode deliveryMode = PulseDeliveryMode.Reliable,
        CancellationToken cancellationToken = default)
    {
        return group.SendEventAsync(handle, data, version, deliveryMode, cancellationToken);
    }
}
