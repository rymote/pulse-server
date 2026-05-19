using System.Collections.Concurrent;
using System.Text.RegularExpressions;
using Rymote.Pulse.Core;
using Rymote.Pulse.Core.Serialization;

namespace Rymote.Pulse.Client;

internal sealed class PulseClientEventHandlerRegistry
{
    private readonly ConcurrentDictionary<string, List<Func<byte[], PulseClientEventContext, Task>>> _literalHandlers = new();
    private readonly List<(Regex Pattern, string[] GroupNames, Func<byte[], PulseClientEventContext, Task> Handler)> _patternHandlers = new();
    private readonly object _patternLock = new();

    public void Register<TEvent>(string handlePattern, Func<TEvent, PulseClientEventContext, Task> handler)
    {
        Func<byte[], PulseClientEventContext, Task> wrappedHandler = async (rawBytes, context) =>
        {
            Core.Messages.PulseEnvelope<TEvent> envelope =
                MsgPackSerdes.Deserialize<Core.Messages.PulseEnvelope<TEvent>>(rawBytes);
            await handler(envelope.Body, context).ConfigureAwait(false);
        };

        if (IsLiteralHandle(handlePattern))
        {
            _literalHandlers.AddOrUpdate(handlePattern,
                _ => new List<Func<byte[], PulseClientEventContext, Task>> { wrappedHandler },
                (_, existing) => { lock (existing) { existing.Add(wrappedHandler); } return existing; });
        }
        else
        {
            HandlePattern pattern = new HandlePattern(handlePattern);
            string[] groupNames = pattern.Regex.GetGroupNames().Where(name => name != "0").ToArray();
            lock (_patternLock)
            {
                _patternHandlers.Add((pattern.Regex, groupNames, wrappedHandler));
            }
        }
    }

    public IReadOnlyList<(Func<byte[], PulseClientEventContext, Task> Handler, Dictionary<string, string> Parameters)>
        ResolveHandlers(string handle)
    {
        List<(Func<byte[], PulseClientEventContext, Task>, Dictionary<string, string>)> matches = new();

        if (_literalHandlers.TryGetValue(handle, out List<Func<byte[], PulseClientEventContext, Task>>? literalList))
        {
            lock (literalList)
            {
                foreach (Func<byte[], PulseClientEventContext, Task> handler in literalList)
                    matches.Add((handler, new Dictionary<string, string>()));
            }
        }

        lock (_patternLock)
        {
            foreach ((Regex regex, string[] groupNames, Func<byte[], PulseClientEventContext, Task> handler) in _patternHandlers)
            {
                Match match = regex.Match(handle);
                if (!match.Success) continue;

                Dictionary<string, string> parameters = new();
                foreach (string groupName in groupNames)
                    if (match.Groups[groupName].Success)
                        parameters[groupName] = match.Groups[groupName].Value;

                matches.Add((handler, parameters));
            }
        }

        return matches;
    }

    private static bool IsLiteralHandle(string handle)
        => !handle.Contains('{') && !handle.Contains('*') && !handle.Contains('[');
}
