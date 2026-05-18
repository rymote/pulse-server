using Rymote.Pulse.Core.Connections;
using Rymote.Pulse.Core.Logging;

namespace Rymote.Pulse.Core.Transport;

public static class PulseConnectionPipeline
{
    public static async Task RunAsync(
        PulseConnection connection,
        PulseDispatcher dispatcher,
        IPulseLogger logger,
        CancellationToken cancellationToken)
    {
        try
        {
            while (true)
            {
                ReadOnlyMemory<byte>? message;

                try
                {
                    message = await connection.TransportConnection
                        .ReceiveMessageAsync(cancellationToken)
                        .ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception receiveException)
                {
                    logger.LogError(
                        $"[{connection.ConnectionId}] Transport receive error",
                        receiveException);
                    break;
                }

                if (message is null) break;

                try
                {
                    await dispatcher.ProcessRawAsync(connection, message.Value.ToArray())
                        .ConfigureAwait(false);
                }
                catch (Exception dispatchException)
                {
                    logger.LogError(
                        $"[{connection.ConnectionId}] Error processing message",
                        dispatchException);
                }
            }
        }
        finally
        {
            try
            {
                await connection.TransportConnection
                    .CloseAsync(1000, "Server closing", CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch (Exception closeException)
            {
                logger.LogDebug(
                    $"[{connection.ConnectionId}] Error closing transport: {closeException.Message}");
            }
        }
    }
}
