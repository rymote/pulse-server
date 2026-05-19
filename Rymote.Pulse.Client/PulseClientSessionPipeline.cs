using Rymote.Pulse.Core.Logging;
using Rymote.Pulse.Core.Messages;
using Rymote.Pulse.Core.Serialization;
using Rymote.Pulse.Core.Transport;

namespace Rymote.Pulse.Client;

internal static class PulseClientSessionPipeline
{
    public static async Task RunAsync(
        IPulseSession session,
        PulseClientEventHandlerRegistry registry,
        IPulseLogger logger,
        CancellationToken cancellationToken)
    {
        Task streamLoop = AcceptStreamsAsync(session, registry, logger, cancellationToken);
        Task datagramLoop = session.Datagrams != null
            ? AcceptDatagramsAsync(session, registry, logger, cancellationToken)
            : Task.CompletedTask;

        await Task.WhenAll(streamLoop, datagramLoop).ConfigureAwait(false);
    }

    private static async Task AcceptStreamsAsync(
        IPulseSession session,
        PulseClientEventHandlerRegistry registry,
        IPulseLogger logger,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            IPulseStream? stream;
            try
            {
                stream = await session.AcceptStreamAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception acceptException)
            {
                logger.LogError($"[{session.SessionId}] Client stream-accept error", acceptException);
                break;
            }

            if (stream is null) break;

            _ = Task.Run(() => HandleIncomingStreamAsync(stream, session, registry, logger, cancellationToken),
                CancellationToken.None);
        }
    }

    private static async Task HandleIncomingStreamAsync(
        IPulseStream stream,
        IPulseSession session,
        PulseClientEventHandlerRegistry registry,
        IPulseLogger logger,
        CancellationToken cancellationToken)
    {
        try
        {
            ReadOnlyMemory<byte>? envelopeBytes = await stream.ReadEnvelopeAsync(cancellationToken).ConfigureAwait(false);
            if (envelopeBytes is null) return;

            byte[] rawBytes = envelopeBytes.Value.ToArray();
            await DispatchEnvelopeAsync(rawBytes, session, registry, logger).ConfigureAwait(false);
        }
        catch (PulseStreamResetException)
        {
            // server aborted the push — nothing more to do
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception dispatchException)
        {
            logger.LogError($"[{session.SessionId}] Error dispatching server-pushed event", dispatchException);
        }
        finally
        {
            await stream.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static async Task AcceptDatagramsAsync(
        IPulseSession session,
        PulseClientEventHandlerRegistry registry,
        IPulseLogger logger,
        CancellationToken cancellationToken)
    {
        IPulseDatagramChannel datagramChannel = session.Datagrams!;

        while (true)
        {
            ReadOnlyMemory<byte>? envelopeBytes;
            try
            {
                envelopeBytes = await datagramChannel.ReceiveDatagramAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception receiveException)
            {
                logger.LogError($"[{session.SessionId}] Datagram receive error", receiveException);
                break;
            }

            if (envelopeBytes is null) break;

            ReadOnlyMemory<byte> capturedBytes = envelopeBytes.Value;
            _ = Task.Run(async () =>
            {
                try
                {
                    await DispatchEnvelopeAsync(capturedBytes.ToArray(), session, registry, logger).ConfigureAwait(false);
                }
                catch (Exception datagramDispatchException)
                {
                    logger.LogError($"[{session.SessionId}] Error dispatching datagram event", datagramDispatchException);
                }
            }, CancellationToken.None);
        }
    }

    private static async Task DispatchEnvelopeAsync(
        byte[] rawBytes,
        IPulseSession session,
        PulseClientEventHandlerRegistry registry,
        IPulseLogger logger)
    {
        PulseEnvelope<object> envelope;
        try
        {
            envelope = MsgPackSerdes.Deserialize<PulseEnvelope<object>>(rawBytes);
        }
        catch (Exception decodeException)
        {
            logger.LogWarning($"[{session.SessionId}] Dropping undecodable inbound envelope: {decodeException.Message}");
            return;
        }

        IReadOnlyList<(Func<byte[], PulseClientEventContext, Task> Handler, Dictionary<string, string> Parameters)> matches =
            registry.ResolveHandlers(envelope.Handle);

        if (matches.Count == 0) return;

        foreach ((Func<byte[], PulseClientEventContext, Task> handler, Dictionary<string, string> parameters) in matches)
        {
            PulseClientEventContext context = new PulseClientEventContext(
                envelope.Handle, parameters, envelope, rawBytes, session.TransportName, logger);

            try
            {
                await handler(rawBytes, context).ConfigureAwait(false);
            }
            catch (Exception handlerException)
            {
                logger.LogError(
                    $"[{session.SessionId}] Event handler for '{envelope.Handle}' threw",
                    handlerException);
            }
        }
    }
}
