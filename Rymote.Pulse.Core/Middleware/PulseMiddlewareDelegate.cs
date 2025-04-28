using System;
using System.Threading.Tasks;

namespace Rymote.Pulse.Core.Middleware;

public delegate Task PulseMiddlewareDelegate(PulseContext context, Func<Task> next);