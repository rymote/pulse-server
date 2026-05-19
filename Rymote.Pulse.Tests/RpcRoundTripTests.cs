using Rymote.Pulse.Core;
using Rymote.Pulse.Core.Connections;
using Rymote.Pulse.Core.Messages;
using Rymote.Pulse.Core.Serialization;
using Rymote.Pulse.Core.Transport;
using Rymote.Pulse.Tests.Helpers;
using Rymote.Pulse.Transports.Multiplexing;
using Xunit;

namespace Rymote.Pulse.Tests;

public class RpcRoundTripTests
{
    [Fact]
    public async Task MapRpc_EchoHandler_ReturnsResponseToClient()
    {
        TestLogger logger = new TestLogger();
        PulseConnectionManager connectionManager = new PulseConnectionManager(logger: logger);
        PulseDispatcher dispatcher = new PulseDispatcher(connectionManager, logger);

        dispatcher.MapRpc<EchoRequest, EchoResponse>("echo",
            (request, context) => Task.FromResult(new EchoResponse { Message = request.Message + " (echo)" }));

        (PulseStreamMultiplexer clientSession, PulseStreamMultiplexer serverSession) =
            PulsePairFactory.CreatePair(logger);

        using CancellationTokenSource testCancellationTokenSource = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        Task serverLifecycleTask = Task.Run(() => PulseSessionLifecycle.HandleAsync(
            serverSession, dispatcher, logger, testCancellationTokenSource.Token));

        // Client opens bidi stream, sends RPC envelope, reads response
        IPulseStream clientStream = await clientSession.OpenStreamAsync(
            PulseStreamDirection.Bidirectional, testCancellationTokenSource.Token);

        PulseEnvelope<EchoRequest> requestEnvelope = new PulseEnvelope<EchoRequest>
        {
            Handle = "echo",
            Body = new EchoRequest { Message = "hello" },
            Kind = PulseKind.RPC,
            Version = "v1"
        };

        await clientStream.WriteEnvelopeAsync(MsgPackSerdes.Serialize(requestEnvelope), testCancellationTokenSource.Token);
        await clientStream.CompleteWritesAsync(testCancellationTokenSource.Token);

        ReadOnlyMemory<byte>? responseBytes = await clientStream.ReadEnvelopeAsync(testCancellationTokenSource.Token);
        Assert.NotNull(responseBytes);

        PulseEnvelope<EchoResponse> responseEnvelope =
            MsgPackSerdes.Deserialize<PulseEnvelope<EchoResponse>>(responseBytes!.Value.ToArray());

        Assert.Equal(PulseStatus.OK, responseEnvelope.Status);
        Assert.Equal("hello (echo)", responseEnvelope.Body.Message);

        await clientStream.DisposeAsync();
        await clientSession.DisposeAsync();
    }

    [Fact]
    public async Task MapRpc_HandlerThrows_ReturnsErrorEnvelope()
    {
        TestLogger logger = new TestLogger();
        PulseConnectionManager connectionManager = new PulseConnectionManager(logger: logger);
        PulseDispatcher dispatcher = new PulseDispatcher(connectionManager, logger);

        dispatcher.MapRpc<EchoRequest, EchoResponse>("fail",
            (request, context) => throw new InvalidOperationException("boom"));

        (PulseStreamMultiplexer clientSession, PulseStreamMultiplexer serverSession) =
            PulsePairFactory.CreatePair(logger);

        using CancellationTokenSource testCancellationTokenSource = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        Task serverLifecycleTask = Task.Run(() => PulseSessionLifecycle.HandleAsync(
            serverSession, dispatcher, logger, testCancellationTokenSource.Token));

        IPulseStream clientStream = await clientSession.OpenStreamAsync(
            PulseStreamDirection.Bidirectional, testCancellationTokenSource.Token);

        PulseEnvelope<EchoRequest> requestEnvelope = new PulseEnvelope<EchoRequest>
        {
            Handle = "fail",
            Body = new EchoRequest { Message = "x" },
            Kind = PulseKind.RPC,
            Version = "v1"
        };

        await clientStream.WriteEnvelopeAsync(MsgPackSerdes.Serialize(requestEnvelope), testCancellationTokenSource.Token);
        await clientStream.CompleteWritesAsync(testCancellationTokenSource.Token);

        ReadOnlyMemory<byte>? responseBytes = await clientStream.ReadEnvelopeAsync(testCancellationTokenSource.Token);
        Assert.NotNull(responseBytes);

        PulseEnvelope<object> errorEnvelope =
            MsgPackSerdes.Deserialize<PulseEnvelope<object>>(responseBytes!.Value.ToArray());

        Assert.Equal(PulseStatus.INTERNAL_ERROR, errorEnvelope.Status);
        Assert.Equal("boom", errorEnvelope.Error);

        await clientStream.DisposeAsync();
        await clientSession.DisposeAsync();
    }

    [Fact]
    public async Task MapRpc_UnknownHandle_ReturnsNotFound()
    {
        TestLogger logger = new TestLogger();
        PulseConnectionManager connectionManager = new PulseConnectionManager(logger: logger);
        PulseDispatcher dispatcher = new PulseDispatcher(connectionManager, logger);

        (PulseStreamMultiplexer clientSession, PulseStreamMultiplexer serverSession) =
            PulsePairFactory.CreatePair(logger);

        using CancellationTokenSource testCancellationTokenSource = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        Task serverLifecycleTask = Task.Run(() => PulseSessionLifecycle.HandleAsync(
            serverSession, dispatcher, logger, testCancellationTokenSource.Token));

        IPulseStream clientStream = await clientSession.OpenStreamAsync(
            PulseStreamDirection.Bidirectional, testCancellationTokenSource.Token);

        PulseEnvelope<EchoRequest> requestEnvelope = new PulseEnvelope<EchoRequest>
        {
            Handle = "unknown.handle",
            Body = new EchoRequest { Message = "x" },
            Kind = PulseKind.RPC,
            Version = "v1"
        };

        await clientStream.WriteEnvelopeAsync(MsgPackSerdes.Serialize(requestEnvelope), testCancellationTokenSource.Token);
        await clientStream.CompleteWritesAsync(testCancellationTokenSource.Token);

        ReadOnlyMemory<byte>? responseBytes = await clientStream.ReadEnvelopeAsync(testCancellationTokenSource.Token);
        Assert.NotNull(responseBytes);

        PulseEnvelope<object> errorEnvelope =
            MsgPackSerdes.Deserialize<PulseEnvelope<object>>(responseBytes!.Value.ToArray());

        Assert.Equal(PulseStatus.NOT_FOUND, errorEnvelope.Status);

        await clientStream.DisposeAsync();
        await clientSession.DisposeAsync();
    }
}
