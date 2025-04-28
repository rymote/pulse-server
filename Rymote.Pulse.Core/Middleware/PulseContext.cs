using System.Collections.Generic;
using Rymote.Pulse.Core.Logging;
using Rymote.Pulse.Core.Messages;

namespace Rymote.Pulse.Core.Middleware;

public class PulseContext
{
    public PulseConnectionManager PulseConnectionManager { get; }
    public IPulseLogger Logger { get; }
    public PulseRequest Request { get; set; }
    public PulseResponse Response { get; set; }
    public Dictionary<string, object> Items { get; } = new();

    public PulseContext(PulseConnectionManager connectionManager, PulseRequest request, IPulseLogger logger)
    {
        PulseConnectionManager = connectionManager;
        Logger = logger;
        Request = request;
        Response = new PulseResponse
        {
            Id = request.Id,
            Response = request.Request,
            Kind = request.Kind,
            Status = PulseStatus.OK
        };
    }
}