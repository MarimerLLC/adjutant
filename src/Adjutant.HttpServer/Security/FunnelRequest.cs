namespace Adjutant.HttpServer.Security;

/// <summary>
/// Recognizes requests that reached the server through a Tailscale Funnel endpoint, that is, from
/// the public internet rather than from inside the tailnet.
///
/// Tailscale's serve/funnel proxy marks funnel traffic with <see cref="HeaderName"/>. The header
/// is only ever used to <em>restrict</em> access: break-glass admin-token authentication is refused
/// when it is present, so the shared admin token is useless to anyone outside the tailnet. A
/// tailnet client that sets the header on its own requests only locks itself out, so a forged
/// header grants nothing. If the proxy ever stopped sending it, behaviour falls back to what it
/// is without Funnel, which is why the public-access runbook checks for it after enabling Funnel.
/// </summary>
public static class FunnelRequest
{
    /// <summary>Set by Tailscale on requests that arrived over Funnel.</summary>
    public const string HeaderName = "Tailscale-Funnel-Request";

    public static bool IsFunnel(HttpContext? context) =>
        context is not null && context.Request.Headers.ContainsKey(HeaderName);
}
