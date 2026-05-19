using Rymote.Pulse.Core;
using Rymote.Pulse.Core.Connections;
using Rymote.Pulse.Core.Messages;
using Rymote.Pulse.Core.Serialization;
using Rymote.Pulse.Core.Transport;
using Rymote.Pulse.Tests.Helpers;
using Rymote.Pulse.Transports.Multiplexing;
using Xunit;

namespace Rymote.Pulse.Tests;

public class RpcStreamTests
{
    [Fact]
    public async Task MapRpcStream_YieldsAllResponseEnvelopes()
    {
        TestLogger logger = new TestLogger();
        PulseConnectionManager connectionManager = new PulseConnectionManager(logger: logger);
        PulseDispatcher dispatcher = new PulseDispatcher(connectionManager, logger);

        dispatcher.MapRpcStream<CounterRequest, CounterResponse>("counter",
            (request, context) => CountAsync(request.Start, request.Count));

        (PulseStreamMultiplexer clientSession, PulseStreamMultiplexer serverSession) =
            PulsePairFactory.CreatePair(logger);

        using CancellationTokenSource testCancellationTokenSource = new CancellationTokenSource(TimeSpan.FromSeconds(10));

        Task serverLifecycleTask = Task.Run(() => PulseSessionLifecycle.HandleAsync(
            serverSession, dispatcher, logger, testCancellationTokenSource.Token));

        IPulseStream clientStream = await clientSession.OpenStreamAsync(
            PulseStreamDirection.Bidirectional, testCancellationTokenSource.Token);

        PulseEnvelope<CounterRequest> requestEnvelope = new PulseEnvelope<CounterRequest>
        {
            Handle = "counter",
            Body = new CounterRequest { Start = 10, Count = 5 },
            Kind = PulseKind.RPC_STREAM,
            Version = "v1"
        };

        await clientStream.WriteEnvelopeAsync(MsgPackSerdes.Serialize(requestEnvelope), testCancellationTokenSource.Token);
        await clientStream.CompleteWritesAsync(testCancellationTokenSource.Token);

        List<int> receivedValues = new List<int>();
        while (true)
        {
            ReadOnlyMemory<byte>? envelopeBytes = await clientStream.ReadEnvelopeAsync(testCancellationTokenSource.Token);
            if (envelopeBytes is null) break;

            PulseEnvelope<CounterResponse> responseEnvelope =
                MsgPackSerdes.Deserialize<PulseEnvelope<CounterResponse>>(envelopeBytes.Value.ToArray());
            receivedValues.Add(responseEnvelope.Body.Value);
        }

        Assert.Equal(new[] { 10, 11, 12, 13, 14 }, receivedValues);

        await clientStream.DisposeAsync();
        await clientSession.DisposeAsync();
    }

    private static async IAsyncEnumerable<CounterResponse> CountAsync(int start, int count)
    {
        for (int index = 0; index < count; index++)
        {
            yield return new CounterResponse { Value = start + index };
            await Task.Yield();
        }
    }
}
