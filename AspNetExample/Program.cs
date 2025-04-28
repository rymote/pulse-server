using Microsoft.AspNetCore.WebSockets;
using Rymote.Pulse.AspNet;
using Rymote.Pulse.Core;
using Rymote.Pulse.Core.Exceptions;
using Rymote.Pulse.Core.Logging;
using Rymote.Pulse.Core.Messages;
using Rymote.Pulse.Core.Middleware;

WebApplicationBuilder builder = WebApplication.CreateBuilder(args);
builder.Services.AddWebSockets(options =>
{
    options.KeepAliveInterval = TimeSpan.FromMinutes(1);
});

PulseConnectionManager connectionManager = new PulseConnectionManager();
PulseConsoleLogger logger = new PulseConsoleLogger();
PulseDispatcher dispatcher = new PulseDispatcher(connectionManager, logger);

ConcurrencyMiddleware concurrencyMiddleware = new ConcurrencyMiddleware(20, TimeSpan.FromSeconds(2));
dispatcher.Use(concurrencyMiddleware.InvokeAsync);

dispatcher.Use(async (context, next) =>
{
    context.Logger.LogInfo($"[Start] {context.Request.Request} (kind={context.Request.Kind}, version={context.Request.Version})");
    await next();
    context.Logger.LogInfo($"[End] Status={context.Response.Status}, Error={context.Response.Error}");
});

dispatcher.MapRpc<LoginRequest, LoginResponse>("pulse://user.auth/login", async (request, context) =>
{
    if (request.Username == "alice" && request.Password == "secret")
        return new LoginResponse { UserId = "123", Token = "fake-token" };
    
    throw new PulseException(PulseStatus.UNAUTHORIZED, "Invalid credentials");
}, version: "v1");

WebApplication app = builder.Build();

app.UseWebSockets();
app.UsePulseWebSocket("/pulse", dispatcher);
app.Run();

public partial class LoginRequest : PulseMessage
{
    public string Username { get; set; } = "";
    
    public string Password { get; set; } = "";
}

public partial class LoginResponse : PulseMessage
{
    public string UserId { get; set; } = "";
    
    public string Token { get; set; } = "";
}