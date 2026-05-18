using Rymote.Pulse.Core.Connections;
using Rymote.Pulse.Core.Logging;

namespace Rymote.Pulse.Core.Transport;

public static class PulseConnectionLifecycle
{
    public static async Task HandleAsync(
        IPulseTransportConnection transportConnection,
        PulseDispatcher dispatcher,
        IPulseLogger logger,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(transportConnection);
        ArgumentNullException.ThrowIfNull(dispatcher);
        ArgumentNullException.ThrowIfNull(logger);

        PulseConnection connection = await dispatcher.ConnectionManager
            .AddConnectionAsync(transportConnection)
            .ConfigureAwait(false);

        foreach (KeyValuePair<string, object> initialMetadataEntry in transportConnection.InitialMetadata)
            connection.SetMetadata(initialMetadataEntry.Key, initialMetadataEntry.Value);
        connection.SetMetadata("connected_at", DateTime.UtcNow);

        string ipAddress = ReadMetadataString(connection, "ip_address");
        string origin = ReadMetadataString(connection, "origin");
        string userAgent = ReadMetadataString(connection, "user_agent");

        logger.LogInfo(
            $"[{connection.ConnectionId}] Client connected: Transport: {connection.TransportName} | IP: {ipAddress} | Origin: {origin} | UserAgent: {userAgent}");

        try
        {
            await dispatcher.ExecuteOnConnectHandlersAsync(connection).ConfigureAwait(false);
        }
        catch (Exception onConnectException)
        {
            logger.LogError(
                $"[{connection.ConnectionId}] OnConnect error",
                onConnectException);

            try
            {
                await transportConnection
                    .CloseAsync(1008, onConnectException.Message, CancellationToken.None)
                    .ConfigureAwait(false);
            }
            catch
            {
                // ignore close errors during OnConnect failure
            }

            await dispatcher.ConnectionManager
                .RemoveConnectionAsync(connection.ConnectionId)
                .ConfigureAwait(false);
            return;
        }

        try
        {
            await PulseConnectionPipeline.RunAsync(connection, dispatcher, logger, cancellationToken)
                .ConfigureAwait(false);
        }
        finally
        {
            try
            {
                await dispatcher.ExecuteOnDisconnectHandlersAsync(connection).ConfigureAwait(false);
            }
            catch (Exception onDisconnectException)
            {
                logger.LogError(
                    $"[{connection.ConnectionId}] OnDisconnect error",
                    onDisconnectException);
            }

            bool connectedAtExists = connection.Metadata.TryGet("connected_at", out DateTime connectedAt);
            TimeSpan connectedDuration =
                DateTime.UtcNow - (connectedAtExists ? connectedAt : DateTime.UtcNow);
            string durationText = connectedDuration.TotalHours >= 24
                ? connectedDuration.ToString(@"d\.hh\:mm\:ss")
                : connectedDuration.ToString(@"hh\:mm\:ss");

            await dispatcher.ConnectionManager
                .RemoveConnectionAsync(connection.ConnectionId)
                .ConfigureAwait(false);

            logger.LogInfo(
                $"[{connection.ConnectionId}] Client disconnected: Transport: {connection.TransportName} | IP: {ipAddress} | Duration: {durationText}");
        }
    }

    private static string ReadMetadataString(PulseConnection connection, string key)
    {
        return connection.Metadata.TryGet(key, out string? value) && value != null
            ? value
            : "Unknown";
    }
}
