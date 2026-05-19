using System.Threading.Channels;

namespace Rymote.Pulse.Tests.Helpers;

internal static class DuplexStreamFactory
{
    public static (Stream clientSide, Stream serverSide) CreatePair()
    {
        Channel<byte[]> clientToServer = Channel.CreateUnbounded<byte[]>();
        Channel<byte[]> serverToClient = Channel.CreateUnbounded<byte[]>();

        Stream clientSide = new InMemoryDuplexStream(incoming: serverToClient, outgoing: clientToServer);
        Stream serverSide = new InMemoryDuplexStream(incoming: clientToServer, outgoing: serverToClient);
        return (clientSide, serverSide);
    }
}
