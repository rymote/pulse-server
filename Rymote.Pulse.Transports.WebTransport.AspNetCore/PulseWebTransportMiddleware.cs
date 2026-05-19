using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Http.Features;
using Rymote.Pulse.Core;
using Rymote.Pulse.Core.Logging;
using Rymote.Pulse.Core.Transport;

namespace Rymote.Pulse.Transports.WebTransport.AspNetCore;

public static class PulseWebTransportMiddleware
{
    /// <summary>
    /// Registers a WebTransport endpoint at <paramref name="webTransportPath"/> for the given dispatcher.
    ///
    /// <para>
    /// <b>Host prerequisites</b> (not configured by this package):
    /// </para>
    /// <list type="bullet">
    ///   <item>Kestrel HTTP/3 enabled (<c>HttpProtocols.Http3</c>) with TLS.</item>
    ///   <item><c>builder.WebHost.ConfigureKestrel(o =&gt; o.ConfigureEndpointDefaults(lo =&gt; lo.Protocols = HttpProtocols.Http3))</c>.</item>
    ///   <item>Real TLS certificate (browser WebTransport rejects self-signed unless <c>serverCertificateHashes</c> is used client-side).</item>
    /// </list>
    ///
    /// <para>
    /// <b>Known limitation:</b> WebTransport datagrams are not exposed on the public
    /// <c>IWebTransportSession</c> in .NET 10, so the session reports <c>Datagrams == null</c>.
    /// Use <see cref="PulseKind.EVENT"/> over uni streams for now.
    /// </para>
    /// </summary>
    public static IApplicationBuilder UsePulseWebTransport(
        this IApplicationBuilder applicationBuilder,
        string webTransportPath,
        PulseDispatcher pulseDispatcher,
        IPulseLogger pulseLogger,
        Action<PulseWebTransportOptions>? configureOptionsAction = null)
    {
        ArgumentNullException.ThrowIfNull(applicationBuilder);
        ArgumentNullException.ThrowIfNull(pulseDispatcher);
        ArgumentNullException.ThrowIfNull(pulseLogger);

        PulseWebTransportOptions options = new PulseWebTransportOptions();
        configureOptionsAction?.Invoke(options);

        return applicationBuilder.Map(webTransportPath, subApplication =>
        {
            subApplication.Use(async (HttpContext httpContext, Func<Task> nextDelegate) =>
            {
                IHttpWebTransportFeature? webTransportFeature = httpContext.Features.Get<IHttpWebTransportFeature>();
                if (webTransportFeature == null || !webTransportFeature.IsWebTransportRequest)
                {
                    pulseLogger.LogWarning(
                        $"Rejected non-WebTransport request on path {webTransportPath}");
                    httpContext.Response.StatusCode = StatusCodes.Status400BadRequest;
                    return;
                }

                IWebTransportSession webTransportSession;
                try
                {
                    webTransportSession = await webTransportFeature.AcceptAsync(httpContext.RequestAborted)
                        .ConfigureAwait(false);
                }
                catch (Exception acceptException)
                {
                    pulseLogger.LogError(
                        $"Failed to accept WebTransport session on {webTransportPath}",
                        acceptException);
                    httpContext.Response.StatusCode = StatusCodes.Status500InternalServerError;
                    return;
                }

                Dictionary<string, string> queryParameters = httpContext.Request.Query.ToDictionary(
                    keyValuePair => keyValuePair.Key,
                    keyValuePair => keyValuePair.Value.ToString());

                Dictionary<string, object> initialMetadata = new Dictionary<string, object>
                {
                    ["webtransport_session"] = webTransportSession,
                    ["http_context"] = httpContext,
                    ["ip_address"] = ResolveClientIpAddress(httpContext),
                    ["user_agent"] = httpContext.Request.Headers["User-Agent"].FirstOrDefault() ?? "Unknown",
                    ["origin"] = httpContext.Request.Headers["Origin"].FirstOrDefault() ?? "Unknown"
                };

                WebTransportPulseSession pulseSession = new WebTransportPulseSession(
                    webTransportSession, options, queryParameters, initialMetadata);

                try
                {
                    await PulseSessionLifecycle.HandleAsync(
                        pulseSession,
                        pulseDispatcher,
                        pulseLogger,
                        httpContext.RequestAborted);
                }
                catch (Exception lifecycleException)
                {
                    pulseLogger.LogError(
                        $"[{pulseSession.SessionId}] Unhandled WebTransport lifecycle exception",
                        lifecycleException);
                }
            });
        });
    }

    private static string ResolveClientIpAddress(HttpContext httpContext)
    {
        string? forwardedFor = httpContext.Request.Headers["X-Forwarded-For"].FirstOrDefault();
        if (!string.IsNullOrEmpty(forwardedFor))
            return forwardedFor.Split(',')[0].Trim();

        string? realIp = httpContext.Request.Headers["X-Real-IP"].FirstOrDefault();
        if (!string.IsNullOrEmpty(realIp))
            return realIp;

        return httpContext.Connection.RemoteIpAddress?.ToString() ?? "Unknown";
    }
}
