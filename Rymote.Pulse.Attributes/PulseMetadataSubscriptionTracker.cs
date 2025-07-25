using Rymote.Pulse.Core.Connections;
using Rymote.Pulse.Core.Metadata;

namespace Rymote.Pulse.Attributes;

internal class PulseMetadataSubscriptionTracker
{
    private readonly List<(string? Key, PulseMetadataChangedEventHandler Handler)> _subscriptions = [];

    public void AddSubscription(string? key, PulseMetadataChangedEventHandler handler)
    {
        _subscriptions.Add((key, handler));
    }

    public void UnsubscribeAll(PulseConnection connection)
    {
        foreach ((string? key, PulseMetadataChangedEventHandler handler) in _subscriptions)
        {
            if (key != null)
                connection.Metadata.Unsubscribe(key, handler);
            else
                connection.Metadata.UnsubscribeGlobal(handler);
        }

        _subscriptions.Clear();
    }
}