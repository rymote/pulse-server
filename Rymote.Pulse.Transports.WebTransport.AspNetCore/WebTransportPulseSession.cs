using Microsoft.AspNetCore.Connections;
using Microsoft.AspNetCore.Http.Features;
using Rymote.Pulse.Core.Transport;

namespace Rymote.Pulse.Transports.WebTransport.AspNetCore;

internal sealed class WebTransportPulseSession : IPulseSession
{
    public string SessionId { get; }
    public string TransportName => "webtransport";
    public bool IsOpen { get; private set; } = true;
    public IReadOnlyDictionary<string, string> QueryParameters { get; }
    public IReadOnlyDictionary<string, object> InitialMetadata { get; }

    /// <summary>
    /// WebTransport datagrams are not exposed via the public <see cref="IWebTransportSession"/>
    /// surface in .NET 10. This package surfaces no datagram channel for now; applications can
    /// fall back to <see cref="Rymote.Pulse.Core.PulseKind.EVENT"/> over uni streams.
    /// </summary>
    public IPulseDatagramChannel? Datagrams => null;

    private readonly IWebTransportSession _webTransportSession;
    private readonly PulseWebTransportOptions _options;
    private int _disposed;

    public WebTransportPulseSession(
        IWebTransportSession webTransportSession,
        PulseWebTransportOptions options,
        IReadOnlyDictionary<string, string>? queryParameters = null,
        IReadOnlyDictionary<string, object>? initialMetadata = null)
    {
        ArgumentNullException.ThrowIfNull(webTransportSession);
        ArgumentNullException.ThrowIfNull(options);

        _webTransportSession = webTransportSession;
        _options = options;
        SessionId = webTransportSession.SessionId.ToString();
        QueryParameters = queryParameters ?? new Dictionary<string, string>();
        InitialMetadata = initialMetadata ?? new Dictionary<string, object>();
    }

    public async ValueTask<IPulseStream?> AcceptStreamAsync(CancellationToken cancellationToken)
    {
        ConnectionContext? streamContext;
        try
        {
            streamContext = await _webTransportSession.AcceptStreamAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return null;
        }
        catch (Exception)
        {
            return null;
        }

        if (streamContext == null) return null;

        // The public IWebTransportSession does not surface per-stream direction; report Bidirectional —
        // the dispatcher reads the first envelope's Kind (EVENT vs. RPC) and the wire semantics work out.
        PulseStreamDirection direction = PulseStreamDirection.Bidirectional;

        long streamId = ResolveStreamId(streamContext);

        return new WebTransportPulseStream(
            streamId,
            direction,
            streamContext.Transport.Input.AsStream(),
            streamContext.Transport.Output.AsStream(),
            _options.MaxStreamEnvelopeSizeInBytes,
            abortAction: () =>
            {
                try { streamContext.Abort(); }
                catch { /* best-effort */ }
            });
    }

    public async ValueTask<IPulseStream> OpenStreamAsync(PulseStreamDirection direction, CancellationToken cancellationToken)
    {
        if (direction == PulseStreamDirection.Bidirectional)
            throw new NotSupportedException(
                "Server-initiated bidirectional WebTransport streams are not exposed in .NET 10's IWebTransportSession.");

        if (direction == PulseStreamDirection.UnidirectionalClientToServer)
            throw new InvalidOperationException(
                "Server cannot open a client-to-server uni stream.");

        ConnectionContext streamContext =
            await _webTransportSession.OpenUnidirectionalStreamAsync(cancellationToken).ConfigureAwait(false)
            ?? throw new InvalidOperationException("WebTransport session refused to open a unidirectional stream.");

        return new WebTransportPulseStream(
            ResolveStreamId(streamContext),
            PulseStreamDirection.UnidirectionalServerToClient,
            streamContext.Transport.Input.AsStream(),
            streamContext.Transport.Output.AsStream(),
            _options.MaxStreamEnvelopeSizeInBytes,
            abortAction: () =>
            {
                try { streamContext.Abort(); }
                catch { /* best-effort */ }
            });
    }

    public ValueTask CloseAsync(int reasonCode, TimeSpan drainTimeout, CancellationToken cancellationToken)
    {
        IsOpen = false;
        try { _webTransportSession.Abort(reasonCode); }
        catch { /* best-effort */ }
        return ValueTask.CompletedTask;
    }

    public ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return ValueTask.CompletedTask;
        IsOpen = false;
        try { _webTransportSession.Abort(0); }
        catch { /* best-effort */ }
        GC.SuppressFinalize(this);
        return ValueTask.CompletedTask;
    }

    private static long ResolveStreamId(ConnectionContext streamContext)
    {
        return long.TryParse(streamContext.ConnectionId, out long parsedStreamId)
            ? parsedStreamId
            : streamContext.ConnectionId.GetHashCode();
    }
}
