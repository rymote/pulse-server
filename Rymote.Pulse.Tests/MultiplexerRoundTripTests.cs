using Rymote.Pulse.Core.Transport;
using Rymote.Pulse.Tests.Helpers;
using Rymote.Pulse.Transports.Multiplexing;
using Xunit;

namespace Rymote.Pulse.Tests;

public class MultiplexerRoundTripTests
{
    [Fact]
    public async Task BidiStream_RoundTripsSingleEnvelope()
    {
        (PulseStreamMultiplexer clientSession, PulseStreamMultiplexer serverSession) = PulsePairFactory.CreatePair();
        await using (clientSession)
        await using (serverSession)
        {
            using CancellationTokenSource testCancellationTokenSource = new CancellationTokenSource(TimeSpan.FromSeconds(5));

            Task serverEchoTask = Task.Run(async () =>
            {
                IPulseStream? serverStream = await serverSession.AcceptStreamAsync(testCancellationTokenSource.Token);
                Assert.NotNull(serverStream);

                ReadOnlyMemory<byte>? incoming = await serverStream!.ReadEnvelopeAsync(testCancellationTokenSource.Token);
                Assert.NotNull(incoming);
                await serverStream.WriteEnvelopeAsync(incoming!.Value, testCancellationTokenSource.Token);
                await serverStream.CompleteWritesAsync(testCancellationTokenSource.Token);
                await serverStream.DisposeAsync();
            });

            IPulseStream clientStream = await clientSession.OpenStreamAsync(
                PulseStreamDirection.Bidirectional, testCancellationTokenSource.Token);
            byte[] payload = "envelope-bytes"u8.ToArray();
            await clientStream.WriteEnvelopeAsync(payload, testCancellationTokenSource.Token);
            await clientStream.CompleteWritesAsync(testCancellationTokenSource.Token);

            ReadOnlyMemory<byte>? response = await clientStream.ReadEnvelopeAsync(testCancellationTokenSource.Token);
            Assert.NotNull(response);
            Assert.Equal(payload, response!.Value.ToArray());

            await serverEchoTask;
        }
    }

    [Fact]
    public async Task BidiStream_RoundTripsMultipleEnvelopes()
    {
        (PulseStreamMultiplexer clientSession, PulseStreamMultiplexer serverSession) = PulsePairFactory.CreatePair();
        await using (clientSession)
        await using (serverSession)
        {
            using CancellationTokenSource testCancellationTokenSource = new CancellationTokenSource(TimeSpan.FromSeconds(5));

            Task serverTask = Task.Run(async () =>
            {
                IPulseStream? serverStream = await serverSession.AcceptStreamAsync(testCancellationTokenSource.Token);
                Assert.NotNull(serverStream);

                ReadOnlyMemory<byte>? incoming = await serverStream!.ReadEnvelopeAsync(testCancellationTokenSource.Token);
                Assert.NotNull(incoming);

                for (int index = 0; index < 5; index++)
                {
                    byte[] response = System.Text.Encoding.UTF8.GetBytes($"response-{index}");
                    await serverStream.WriteEnvelopeAsync(response, testCancellationTokenSource.Token);
                }
                await serverStream.CompleteWritesAsync(testCancellationTokenSource.Token);
                await serverStream.DisposeAsync();
            });

            IPulseStream clientStream = await clientSession.OpenStreamAsync(
                PulseStreamDirection.Bidirectional, testCancellationTokenSource.Token);
            await clientStream.WriteEnvelopeAsync("request"u8.ToArray(), testCancellationTokenSource.Token);
            await clientStream.CompleteWritesAsync(testCancellationTokenSource.Token);

            List<string> received = new List<string>();
            while (true)
            {
                ReadOnlyMemory<byte>? response = await clientStream.ReadEnvelopeAsync(testCancellationTokenSource.Token);
                if (response is null) break;
                received.Add(System.Text.Encoding.UTF8.GetString(response.Value.Span));
            }

            Assert.Equal(new[] { "response-0", "response-1", "response-2", "response-3", "response-4" }, received);

            await serverTask;
        }
    }

    [Fact]
    public async Task UniStream_ClientToServer_DeliversEnvelope()
    {
        (PulseStreamMultiplexer clientSession, PulseStreamMultiplexer serverSession) = PulsePairFactory.CreatePair();
        await using (clientSession)
        await using (serverSession)
        {
            using CancellationTokenSource testCancellationTokenSource = new CancellationTokenSource(TimeSpan.FromSeconds(5));

            Task<byte[]> receiveTask = Task.Run(async () =>
            {
                IPulseStream? serverStream = await serverSession.AcceptStreamAsync(testCancellationTokenSource.Token);
                Assert.NotNull(serverStream);
                Assert.Equal(PulseStreamDirection.UnidirectionalClientToServer, serverStream!.Direction);

                ReadOnlyMemory<byte>? envelope = await serverStream.ReadEnvelopeAsync(testCancellationTokenSource.Token);
                Assert.NotNull(envelope);
                await serverStream.DisposeAsync();
                return envelope!.Value.ToArray();
            });

            IPulseStream clientStream = await clientSession.OpenStreamAsync(
                PulseStreamDirection.UnidirectionalClientToServer, testCancellationTokenSource.Token);
            byte[] payload = "uni-event"u8.ToArray();
            await clientStream.WriteEnvelopeAsync(payload, testCancellationTokenSource.Token);
            await clientStream.CompleteWritesAsync(testCancellationTokenSource.Token);

            byte[] received = await receiveTask;
            Assert.Equal(payload, received);
        }
    }

    [Fact]
    public async Task Datagram_RoundTripsEnvelope()
    {
        (PulseStreamMultiplexer clientSession, PulseStreamMultiplexer serverSession) = PulsePairFactory.CreatePair();
        await using (clientSession)
        await using (serverSession)
        {
            using CancellationTokenSource testCancellationTokenSource = new CancellationTokenSource(TimeSpan.FromSeconds(5));

            Assert.NotNull(serverSession.Datagrams);
            Assert.NotNull(clientSession.Datagrams);

            Task<byte[]?> receiveTask = Task.Run(async () =>
            {
                ReadOnlyMemory<byte>? datagram = await serverSession.Datagrams!
                    .ReceiveDatagramAsync(testCancellationTokenSource.Token);
                return datagram?.ToArray();
            });

            byte[] payload = "datagram-payload"u8.ToArray();
            await clientSession.Datagrams!.SendDatagramAsync(payload, testCancellationTokenSource.Token);

            byte[]? received = await receiveTask;
            Assert.NotNull(received);
            Assert.Equal(payload, received);
        }
    }

    [Fact]
    public async Task StreamIdAllocation_ClientUsesOdd_ServerUsesEven()
    {
        (PulseStreamMultiplexer clientSession, PulseStreamMultiplexer serverSession) = PulsePairFactory.CreatePair();
        await using (clientSession)
        await using (serverSession)
        {
            using CancellationTokenSource testCancellationTokenSource = new CancellationTokenSource(TimeSpan.FromSeconds(5));

            IPulseStream clientStream1 = await clientSession.OpenStreamAsync(
                PulseStreamDirection.Bidirectional, testCancellationTokenSource.Token);
            IPulseStream clientStream2 = await clientSession.OpenStreamAsync(
                PulseStreamDirection.Bidirectional, testCancellationTokenSource.Token);

            Assert.Equal(1, clientStream1.StreamId % 2);
            Assert.Equal(1, clientStream2.StreamId % 2);

            await clientStream1.DisposeAsync();
            await clientStream2.DisposeAsync();
        }
    }

    [Fact]
    public async Task LargeEnvelope_RoundTrips()
    {
        (PulseStreamMultiplexer clientSession, PulseStreamMultiplexer serverSession) = PulsePairFactory.CreatePair();
        await using (clientSession)
        await using (serverSession)
        {
            using CancellationTokenSource testCancellationTokenSource = new CancellationTokenSource(TimeSpan.FromSeconds(10));

            byte[] largePayload = new byte[256 * 1024];
            new Random(42).NextBytes(largePayload);

            Task<byte[]?> serverTask = Task.Run(async () =>
            {
                IPulseStream? serverStream = await serverSession.AcceptStreamAsync(testCancellationTokenSource.Token);
                Assert.NotNull(serverStream);
                ReadOnlyMemory<byte>? incoming = await serverStream!.ReadEnvelopeAsync(testCancellationTokenSource.Token);
                await serverStream.DisposeAsync();
                return incoming?.ToArray();
            });

            IPulseStream clientStream = await clientSession.OpenStreamAsync(
                PulseStreamDirection.Bidirectional, testCancellationTokenSource.Token);
            await clientStream.WriteEnvelopeAsync(largePayload, testCancellationTokenSource.Token);
            await clientStream.CompleteWritesAsync(testCancellationTokenSource.Token);
            await clientStream.DisposeAsync();

            byte[]? received = await serverTask;
            Assert.NotNull(received);
            Assert.Equal(largePayload, received);
        }
    }
}
