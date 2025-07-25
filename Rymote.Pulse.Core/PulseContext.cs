using System;
using System.Collections.Generic;
using System.Net.WebSockets;
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
    internal Func<byte[], Task>? SendChunkAsync { get; set; }
    public object? TypedResponseEnvelope { get; set; }
    public Dictionary<string, string> Parameters { get; internal set; }

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
        catch (InvalidCastException ex)
        {
            throw new InvalidOperationException($"Cannot convert parameter '{name}' to type {typeof(TParameter).Name}.",
                ex);
        }
    }

    public PulseContext(PulseConnectionManager connectionManager, PulseConnection connection, byte[] rawBytes,
        IPulseLogger logger, Dictionary<string, string> parameters)
    {
        ConnectionManager = connectionManager;
        Connection = connection;
        Logger = logger;
        RawRequestBytes = rawBytes;
        UntypedRequest = MsgPackSerdes.Deserialize<PulseEnvelope<object>>(rawBytes);
        Parameters = parameters;
    }

    public PulseEnvelope<T> GetTypedRequestEnvelope<T>()
    {
        return MsgPackSerdes.Deserialize<PulseEnvelope<T>>(RawRequestBytes);
    }

    public async Task PipeStreamAsync(Stream inputStream, int chunkSize = 8192,
        CancellationToken cancellationToken = default)
    {
        if (SendChunkAsync == null)
        {
            throw new InvalidOperationException("SendChunkAsync is not configured.");
        }

        byte[] buffer = new byte[chunkSize];
        int bytesRead;

        while ((bytesRead = await inputStream.ReadAsync(buffer, cancellationToken)) > 0)
        {
            byte[] chunkData = buffer.AsSpan(0, bytesRead).ToArray();
            PulseEnvelope<byte[]> chunkEnvelope = new PulseEnvelope<byte[]>
            {
                Handle = UntypedRequest.Handle,
                Body = chunkData,
                AuthToken = UntypedRequest.AuthToken,
                Kind = PulseKind.STREAM,
                Version = UntypedRequest.Version,
                ClientCorrelationId = UntypedRequest.ClientCorrelationId,
                IsStreamChunk = true,
                EndOfStream = false
            };

            byte[] packed = MsgPackSerdes.Serialize(chunkEnvelope);
            await SendChunkAsync(packed);
        }

        PulseEnvelope<byte[]> endOfStreamEnvelope = new PulseEnvelope<byte[]>
        {
            Handle = UntypedRequest.Handle,
            Body = [],
            AuthToken = UntypedRequest.AuthToken,
            Kind = PulseKind.STREAM,
            Version = UntypedRequest.Version,
            ClientCorrelationId = UntypedRequest.ClientCorrelationId,
            IsStreamChunk = true,
            EndOfStream = true
        };

        byte[] eosPacked = MsgPackSerdes.Serialize(endOfStreamEnvelope);
        await SendChunkAsync(eosPacked);
    }

    public Task SendEventAsync<TPayload>(
        string handle,
        TPayload data,
        string version = "v1",
        CancellationToken cancellationToken = default
    ) where TPayload : class, new()
    {
        return Connection.SendEventAsync(handle, data, version, cancellationToken);
    }

    public Task SendEventAsync<TPayload>(
        PulseConnection connection,
        string handle,
        TPayload data,
        string version = "v1",
        CancellationToken cancellationToken = default
    ) where TPayload : class, new()
    {
        return connection.SendEventAsync(handle, data, version, cancellationToken);
    }

    public Task SendEventAsync<TPayload>(
        PulseGroup group,
        string handle,
        TPayload data,
        string version = "v1",
        CancellationToken cancellationToken = default
    ) where TPayload : class, new()
    {
        return group.SendEventAsync(handle, data, version, cancellationToken);
    }
}