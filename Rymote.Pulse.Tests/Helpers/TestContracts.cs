using MessagePack;

namespace Rymote.Pulse.Tests.Helpers;

[MessagePackObject]
public class EchoRequest
{
    [Key(0)] public string Message { get; set; } = string.Empty;
}

[MessagePackObject]
public class EchoResponse
{
    [Key(0)] public string Message { get; set; } = string.Empty;
}

[MessagePackObject]
public class CounterRequest
{
    [Key(0)] public int Start { get; set; }
    [Key(1)] public int Count { get; set; }
}

[MessagePackObject]
public class CounterResponse
{
    [Key(0)] public int Value { get; set; }
}

[MessagePackObject]
public class NotificationEvent
{
    [Key(0)] public string Text { get; set; } = string.Empty;
}
