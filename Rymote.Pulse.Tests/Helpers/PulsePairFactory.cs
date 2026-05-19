using Rymote.Pulse.Core.Logging;
using Rymote.Pulse.Transports.Multiplexing;

namespace Rymote.Pulse.Tests.Helpers;

internal static class PulsePairFactory
{
    public static (PulseStreamMultiplexer clientSession, PulseStreamMultiplexer serverSession) CreatePair(
        IPulseLogger? logger = null,
        PulseStreamMultiplexerOptions? options = null)
    {
        logger ??= new TestLogger();
        options ??= new PulseStreamMultiplexerOptions();

        (Stream clientStream, Stream serverStream) = DuplexStreamFactory.CreatePair();

        PulseStreamMultiplexer serverSession = new PulseStreamMultiplexer(
            serverStream, isServerSide: true, transportName: "in-memory",
            logger: logger, options: options);

        PulseStreamMultiplexer clientSession = new PulseStreamMultiplexer(
            clientStream, isServerSide: false, transportName: "in-memory",
            logger: logger, options: options);

        serverSession.Start();
        clientSession.Start();

        return (clientSession, serverSession);
    }
}
