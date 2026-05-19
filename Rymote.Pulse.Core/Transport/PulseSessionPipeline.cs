using Rymote.Pulse.Core.Connections;
using Rymote.Pulse.Core.Logging;

namespace Rymote.Pulse.Core.Transport;

public static class PulseSessionPipeline
{
    public static async Task RunAsync(
        PulseConnection connection,
        PulseDispatcher dispatcher,
        IPulseLogger logger,
        CancellationToken cancellationToken)
    {
        Task streamLoop = AcceptStreamsAsync(connection, dispatcher, logger, cancellationToken);
        Task datagramLoop = connection.Session.Datagrams != null
            ? AcceptDatagramsAsync(connection, dispatcher, logger, cancellationToken)
            : Task.CompletedTask;

        await Task.WhenAll(streamLoop, datagramLoop).ConfigureAwait(false);
    }

    private static async Task AcceptStreamsAsync(
        PulseConnection connection,
        PulseDispatcher dispatcher,
        IPulseLogger logger,
        CancellationToken cancellationToken)
    {
        while (true)
        {
            IPulseStream? stream;
            try
            {
                stream = await connection.Session.AcceptStreamAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception acceptException)
            {
                logger.LogError($"[{connection.ConnectionId}] Session stream-accept error", acceptException);
                break;
            }

            if (stream is null) break;

            _ = Task.Run(() => DispatchStreamSafelyAsync(stream, connection, dispatcher, logger, cancellationToken),
                CancellationToken.None);
        }
    }

    private static async Task DispatchStreamSafelyAsync(
        IPulseStream stream,
        PulseConnection connection,
        PulseDispatcher dispatcher,
        IPulseLogger logger,
        CancellationToken cancellationToken)
    {
        try
        {
            await dispatcher.ProcessStreamAsync(stream, connection, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception dispatchException)
        {
            logger.LogError(
                $"[{connection.ConnectionId}/stream {stream.StreamId}] Unhandled dispatch error",
                dispatchException);
            try { await stream.AbortAsync(reasonCode: 0, CancellationToken.None).ConfigureAwait(false); }
            catch { /* ignore */ }
        }
        finally
        {
            await stream.DisposeAsync().ConfigureAwait(false);
        }
    }

    private static async Task AcceptDatagramsAsync(
        PulseConnection connection,
        PulseDispatcher dispatcher,
        IPulseLogger logger,
        CancellationToken cancellationToken)
    {
        IPulseDatagramChannel datagramChannel = connection.Session.Datagrams!;

        while (true)
        {
            ReadOnlyMemory<byte>? envelopeBytes;
            try
            {
                envelopeBytes = await datagramChannel.ReceiveDatagramAsync(cancellationToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception receiveException)
            {
                logger.LogError($"[{connection.ConnectionId}] Datagram receive error", receiveException);
                break;
            }

            if (envelopeBytes is null) break;

            ReadOnlyMemory<byte> capturedEnvelopeBytes = envelopeBytes.Value;
            _ = Task.Run(async () =>
            {
                try
                {
                    await dispatcher.ProcessDatagramAsync(capturedEnvelopeBytes, connection, cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (Exception datagramException)
                {
                    logger.LogError($"[{connection.ConnectionId}] Datagram dispatch error", datagramException);
                }
            }, CancellationToken.None);
        }
    }
}
