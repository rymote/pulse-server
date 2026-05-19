using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Threading.Channels;
using Rymote.Pulse.Core.Logging;
using Rymote.Pulse.Core.Transport;

namespace Rymote.Pulse.Transports.Multiplexing;

public sealed class PulseStreamMultiplexer : IPulseSession
{
    public string SessionId { get; }
    public string TransportName { get; }
    public bool IsOpen => _disposed == 0;
    public IReadOnlyDictionary<string, string> QueryParameters { get; }
    public IReadOnlyDictionary<string, object> InitialMetadata { get; }
    public IPulseDatagramChannel? Datagrams => _datagrams;

    internal bool IsServerSide { get; }

    private readonly Stream _byteChannel;
    private readonly IPulseLogger _logger;
    private readonly PulseStreamMultiplexerOptions _options;
    private readonly bool _ownsByteChannel;

    private readonly ConcurrentDictionary<long, VirtualPulseStream> _activeStreams = new();
    private readonly Channel<IPulseStream> _incomingStreams;
    private readonly Channel<MultiplexerOutboundFrame> _outboundFrames;
    private readonly MultiplexedDatagramChannel? _datagrams;

    private long _nextOutgoingStreamId;
    private readonly CancellationTokenSource _shutdownCts = new();
    private Task? _readerTask;
    private Task? _writerTask;
    private int _disposed;

    public PulseStreamMultiplexer(
        Stream byteChannel,
        bool isServerSide,
        string transportName,
        IPulseLogger logger,
        PulseStreamMultiplexerOptions options,
        IReadOnlyDictionary<string, string>? queryParameters = null,
        IReadOnlyDictionary<string, object>? initialMetadata = null,
        bool ownsByteChannel = true)
    {
        ArgumentNullException.ThrowIfNull(byteChannel);
        ArgumentNullException.ThrowIfNull(logger);
        ArgumentNullException.ThrowIfNull(options);

        _byteChannel = byteChannel;
        IsServerSide = isServerSide;
        TransportName = transportName;
        SessionId = Guid.NewGuid().ToString();
        _logger = logger;
        _options = options;
        _ownsByteChannel = ownsByteChannel;

        _nextOutgoingStreamId = isServerSide ? 0 : -1;

        _incomingStreams = Channel.CreateUnbounded<IPulseStream>();
        _outboundFrames = Channel.CreateUnbounded<MultiplexerOutboundFrame>();

        if (options.DatagramsEnabled)
            _datagrams = new MultiplexedDatagramChannel(this, options.MaxDatagramEnvelopeSizeInBytes);

        QueryParameters = queryParameters ?? new Dictionary<string, string>();
        InitialMetadata = initialMetadata ?? new Dictionary<string, object>();
    }

    public void Start()
    {
        _readerTask = Task.Run(() => RunReaderAsync(_shutdownCts.Token));
        _writerTask = Task.Run(() => RunWriterAsync(_shutdownCts.Token));
    }

    public async ValueTask<IPulseStream?> AcceptStreamAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (await _incomingStreams.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
                return await _incomingStreams.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (ChannelClosedException)
        {
        }
        return null;
    }

    public async ValueTask<IPulseStream> OpenStreamAsync(PulseStreamDirection direction, CancellationToken cancellationToken)
    {
        long streamId = Interlocked.Add(ref _nextOutgoingStreamId, 2);

        VirtualPulseStream stream = new VirtualPulseStream(this, streamId, direction);
        _activeStreams[streamId] = stream;

        byte op = direction == PulseStreamDirection.Bidirectional
            ? MultiplexerFrameOps.OPEN_BIDI
            : MultiplexerFrameOps.OPEN_UNI;

        await QueueOutboundFrameAsync(
            new MultiplexerOutboundFrame(streamId, op, ReadOnlyMemory<byte>.Empty),
            cancellationToken).ConfigureAwait(false);

        return stream;
    }

    public async ValueTask CloseAsync(int reasonCode, TimeSpan drainTimeout, CancellationToken cancellationToken)
    {
        byte[] goawayPayload = new byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(
            goawayPayload, (uint)Math.Max(0, (int)drainTimeout.TotalMilliseconds));

        try
        {
            await QueueOutboundFrameAsync(
                new MultiplexerOutboundFrame(0, MultiplexerFrameOps.GOAWAY, goawayPayload),
                cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            // best-effort GOAWAY
        }

        if (drainTimeout > TimeSpan.Zero)
        {
            try
            {
                await Task.Delay(drainTimeout, cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
            }
        }

        await DisposeAsync().ConfigureAwait(false);
    }

    internal ValueTask QueueOutboundFrameAsync(MultiplexerOutboundFrame frame, CancellationToken cancellationToken)
    {
        return _outboundFrames.Writer.WriteAsync(frame, cancellationToken);
    }

    internal void OnStreamDisposed(long streamId)
    {
        _activeStreams.TryRemove(streamId, out _);
    }

    private async Task RunReaderAsync(CancellationToken cancellationToken)
    {
        byte[] headerBuffer = new byte[9];

        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                if (!await ReadExactlyAsync(headerBuffer, cancellationToken).ConfigureAwait(false))
                    break;

                uint streamId = BinaryPrimitives.ReadUInt32BigEndian(headerBuffer.AsSpan(0, 4));
                byte op = headerBuffer[4];
                uint payloadLength = BinaryPrimitives.ReadUInt32BigEndian(headerBuffer.AsSpan(5, 4));

                if (payloadLength > _options.MaxFramePayloadSizeInBytes)
                {
                    _logger.LogError(
                        $"Multiplexer: frame too large ({payloadLength} > {_options.MaxFramePayloadSizeInBytes})");
                    break;
                }

                byte[] payload;
                if (payloadLength == 0)
                {
                    payload = Array.Empty<byte>();
                }
                else
                {
                    payload = new byte[(int)payloadLength];
                    if (!await ReadExactlyAsync(payload, cancellationToken).ConfigureAwait(false))
                        break;
                }

                RouteFrame(streamId, op, payload);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception readerException)
        {
            _logger.LogError("Multiplexer reader error", readerException);
        }
        finally
        {
            CompleteIncomingState();
        }
    }

    private async ValueTask<bool> ReadExactlyAsync(byte[] buffer, CancellationToken cancellationToken)
    {
        int bytesRead = 0;
        while (bytesRead < buffer.Length)
        {
            int chunk;
            try
            {
                chunk = await _byteChannel.ReadAsync(
                    buffer.AsMemory(bytesRead, buffer.Length - bytesRead),
                    cancellationToken).ConfigureAwait(false);
            }
            catch (IOException) { return false; }
            catch (ObjectDisposedException) { return false; }
            catch (OperationCanceledException) { return false; }

            if (chunk == 0) return false;
            bytesRead += chunk;
        }
        return true;
    }

    private void RouteFrame(long streamId, byte op, ReadOnlyMemory<byte> payload)
    {
        switch (op)
        {
            case MultiplexerFrameOps.OPEN_BIDI:
                OpenIncomingStream(streamId, PulseStreamDirection.Bidirectional);
                break;

            case MultiplexerFrameOps.OPEN_UNI:
                PulseStreamDirection uniDirection = IsServerSide
                    ? PulseStreamDirection.UnidirectionalClientToServer
                    : PulseStreamDirection.UnidirectionalServerToClient;
                OpenIncomingStream(streamId, uniDirection);
                break;

            case MultiplexerFrameOps.DATA:
                if (_activeStreams.TryGetValue(streamId, out VirtualPulseStream? dataStream))
                    dataStream.TryEnqueueIncomingEnvelope(payload);
                break;

            case MultiplexerFrameOps.FIN:
                if (_activeStreams.TryGetValue(streamId, out VirtualPulseStream? finStream))
                    finStream.OnFinFromPeer();
                break;

            case MultiplexerFrameOps.RESET:
                int reasonCode = payload.Length >= 4
                    ? (int)BinaryPrimitives.ReadUInt32BigEndian(payload.Span)
                    : 0;
                if (_activeStreams.TryRemove(streamId, out VirtualPulseStream? resetStream))
                    resetStream.OnResetFromPeer(reasonCode);
                break;

            case MultiplexerFrameOps.DATAGRAM:
                _datagrams?.EnqueueIncoming(payload);
                break;

            case MultiplexerFrameOps.PING:
                _outboundFrames.Writer.TryWrite(
                    new MultiplexerOutboundFrame(0, MultiplexerFrameOps.PONG, payload));
                break;

            case MultiplexerFrameOps.PONG:
                // keepalive timer reset would go here
                break;

            case MultiplexerFrameOps.GOAWAY:
                _logger.LogInfo("Multiplexer: GOAWAY received from peer");
                CompleteIncomingState();
                break;

            default:
                _logger.LogWarning($"Multiplexer: unknown op 0x{op:x2}");
                break;
        }
    }

    private void OpenIncomingStream(long streamId, PulseStreamDirection direction)
    {
        VirtualPulseStream stream = new VirtualPulseStream(this, streamId, direction);
        if (_activeStreams.TryAdd(streamId, stream))
        {
            _incomingStreams.Writer.TryWrite(stream);
        }
        else
        {
            _ = QueueOutboundFrameAsync(
                MultiplexerOutboundFrame.Reset(streamId, reasonCode: 3),
                CancellationToken.None);
        }
    }

    private async Task RunWriterAsync(CancellationToken cancellationToken)
    {
        byte[] headerBuffer = new byte[9];

        try
        {
            await foreach (MultiplexerOutboundFrame frame in
                _outboundFrames.Reader.ReadAllAsync(cancellationToken).ConfigureAwait(false))
            {
                BinaryPrimitives.WriteUInt32BigEndian(headerBuffer.AsSpan(0, 4), (uint)frame.StreamId);
                headerBuffer[4] = frame.Op;
                BinaryPrimitives.WriteUInt32BigEndian(headerBuffer.AsSpan(5, 4), (uint)frame.Payload.Length);

                await _byteChannel.WriteAsync(headerBuffer.AsMemory(), cancellationToken).ConfigureAwait(false);
                if (frame.Payload.Length > 0)
                    await _byteChannel.WriteAsync(frame.Payload, cancellationToken).ConfigureAwait(false);

                await _byteChannel.FlushAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception writerException)
        {
            _logger.LogError("Multiplexer writer error", writerException);
        }
    }

    private void CompleteIncomingState()
    {
        _incomingStreams.Writer.TryComplete();
        foreach (KeyValuePair<long, VirtualPulseStream> entry in _activeStreams)
            entry.Value.OnFinFromPeer();
        _datagrams?.Complete();
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;

        _shutdownCts.Cancel();
        _outboundFrames.Writer.TryComplete();
        _incomingStreams.Writer.TryComplete();
        _datagrams?.Complete();

        try { if (_readerTask != null) await _readerTask.ConfigureAwait(false); }
        catch { /* shutdown */ }
        try { if (_writerTask != null) await _writerTask.ConfigureAwait(false); }
        catch { /* shutdown */ }

        if (_ownsByteChannel)
        {
            try { await _byteChannel.DisposeAsync().ConfigureAwait(false); }
            catch { /* shutdown */ }
        }

        _shutdownCts.Dispose();
        GC.SuppressFinalize(this);
    }
}
