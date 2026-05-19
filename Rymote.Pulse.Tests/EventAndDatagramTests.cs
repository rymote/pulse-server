using Rymote.Pulse.Core;
using Rymote.Pulse.Core.Connections;
using Rymote.Pulse.Core.Messages;
using Rymote.Pulse.Core.Serialization;
using Rymote.Pulse.Core.Transport;
using Rymote.Pulse.Tests.Helpers;
using Rymote.Pulse.Transports.Multiplexing;
using Xunit;

namespace Rymote.Pulse.Tests;

public class EventAndDatagramTests
{
    [Fact]
    public async Task MapEvent_OverUniStream_HandlerReceivesPayload()
    {
        TestLogger logger = new TestLogger();
        PulseConnectionManager connectionManager = new PulseConnectionManager(logger: logger);
        PulseDispatcher dispatcher = new PulseDispatcher(connectionManager, logger);

        TaskCompletionSource<string> handlerCompletionSource = new TaskCompletionSource<string>();
        dispatcher.MapEvent<NotificationEvent>("notify",
            (notification, context) =>
            {
                handlerCompletionSource.TrySetResult(notification.Text);
                return Task.CompletedTask;
            });

        (PulseStreamMultiplexer clientSession, PulseStreamMultiplexer serverSession) =
            PulsePairFactory.CreatePair(logger);

        using CancellationTokenSource testCancellationTokenSource = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        Task serverLifecycleTask = Task.Run(() => PulseSessionLifecycle.HandleAsync(
            serverSession, dispatcher, logger, testCancellationTokenSource.Token));

        IPulseStream clientStream = await clientSession.OpenStreamAsync(
            PulseStreamDirection.UnidirectionalClientToServer, testCancellationTokenSource.Token);

        PulseEnvelope<NotificationEvent> eventEnvelope = new PulseEnvelope<NotificationEvent>
        {
            Handle = "notify",
            Body = new NotificationEvent { Text = "hello-event" },
            Kind = PulseKind.EVENT,
            Version = "v1"
        };

        await clientStream.WriteEnvelopeAsync(MsgPackSerdes.Serialize(eventEnvelope), testCancellationTokenSource.Token);
        await clientStream.CompleteWritesAsync(testCancellationTokenSource.Token);
        await clientStream.DisposeAsync();

        string received = await handlerCompletionSource.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("hello-event", received);

        await clientSession.DisposeAsync();
    }

    [Fact]
    public async Task MapEvent_OverDatagram_HandlerReceivesPayload()
    {
        TestLogger logger = new TestLogger();
        PulseConnectionManager connectionManager = new PulseConnectionManager(logger: logger);
        PulseDispatcher dispatcher = new PulseDispatcher(connectionManager, logger);

        TaskCompletionSource<string> handlerCompletionSource = new TaskCompletionSource<string>();
        dispatcher.MapEvent<NotificationEvent>("notify.lossy",
            (notification, context) =>
            {
                handlerCompletionSource.TrySetResult(notification.Text);
                return Task.CompletedTask;
            });

        (PulseStreamMultiplexer clientSession, PulseStreamMultiplexer serverSession) =
            PulsePairFactory.CreatePair(logger);

        using CancellationTokenSource testCancellationTokenSource = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        Task serverLifecycleTask = Task.Run(() => PulseSessionLifecycle.HandleAsync(
            serverSession, dispatcher, logger, testCancellationTokenSource.Token));

        Assert.NotNull(clientSession.Datagrams);

        PulseEnvelope<NotificationEvent> eventEnvelope = new PulseEnvelope<NotificationEvent>
        {
            Handle = "notify.lossy",
            Body = new NotificationEvent { Text = "lossy-event" },
            Kind = PulseKind.DATAGRAM_EVENT,
            Version = "v1"
        };

        await clientSession.Datagrams!.SendDatagramAsync(MsgPackSerdes.Serialize(eventEnvelope), testCancellationTokenSource.Token);

        string received = await handlerCompletionSource.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal("lossy-event", received);

        await clientSession.DisposeAsync();
    }
}
