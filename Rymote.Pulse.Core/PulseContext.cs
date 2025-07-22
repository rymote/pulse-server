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

    public T? GetParameter<T>(string name)
    {
        if (Parameters.TryGetValue(name, out string? value))
        {
            try
            {
                return (T)Convert.ChangeType(value, typeof(T));
            }
            catch (InvalidCastException)
            {
                return default(T);
            }
        }
        return default(T);
    }

    public T GetRequiredParameter<T>(string name)
    {
        if (Parameters.TryGetValue(name, out string? value))
        {
            try
            {
                return (T)Convert.ChangeType(value, typeof(T));
            }
            catch (InvalidCastException ex)
            {
                throw new InvalidOperationException($"Cannot convert parameter '{name}' to type {typeof(T).Name}.", ex);
            }
        }
        throw new KeyNotFoundException($"Required parameter '{name}' not found.");
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

    public Task SendEventAsync<T>(
        string handle,
        T data,
        string version = "v1",
        CancellationToken cancellationToken = default
    ) where T : class, new()
    {
        return SendEventAsync(Connection, handle, data, version, cancellationToken);
    }

    public async Task SendEventAsync<T>(
        PulseConnection connection,
        string handle,
        T data,
        string version = "v1",
        CancellationToken cancellationToken = default
    ) where T : class, new()
    {
        PulseEnvelope<T> envelope = new PulseEnvelope<T>
        {
            Handle = handle,
            Body = data,
            Kind = PulseKind.EVENT,
            Version = version
        };

        byte[] envelopeBytes = MsgPackSerdes.Serialize(envelope);
        await connection.SendAsync(envelopeBytes, cancellationToken);
    }
    
    public async Task SendEventAsync<T>(
        PulseGroup group,
        string handle,
        T data,
        string version = "v1",
        CancellationToken cancellationToken = default
    ) where T : class, new()
    {
        PulseEnvelope<T> envelope = new PulseEnvelope<T>
        {
            Handle = handle,
            Body = data,
            Kind = PulseKind.EVENT,
            Version = version
        };

        byte[] envelopeBytes = MsgPackSerdes.Serialize(envelope);
        await group.BroadcastAsync(envelopeBytes, cancellationToken);
    }
}