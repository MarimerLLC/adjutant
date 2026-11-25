using Adjutant.Core.Configuration;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authentication.OpenIdConnect;
using Microsoft.Extensions.Options;

namespace Adjutant.HttpServer.BlazorAdmin;

/// <summary>
/// Minimal API endpoints for admin authentication actions that require HTTP responses
/// (sign-in redirects and logout manipulate cookies and issue redirects, which cannot be done
/// over the Blazor SignalR circuit).
/// </summary>
public static class AdminAuthEndpoints
{
    public static WebApplication MapAdminAuthEndpoints(this WebApplication app)
    {
        // Starts an OIDC sign-in. Reached by a plain link from the login page, so it is a GET
        // with no antiforgery token — it mutates nothing and the OIDC state parameter carries
        // the CSRF protection for the round trip.
        // returnUrl carries an MCP client's authorization request through the provider round trip;
        // only a local path is honoured, so this cannot be turned into an open redirect.
        app.MapGet("/admin/auth/login/{scheme}", async (
            HttpContext context,
            string scheme,
            string? returnUrl,
            IOptionsMonitor<AdminAuthConfiguration> adminAuth,
            IOptionsMonitor<OpenIdConnectOptions> oidc,
            ILoggerFactory loggers) =>
        {
            if (!AdminOidc.KnownSchemes.Contains(scheme, StringComparer.Ordinal))
                return Results.Redirect(AdminSignInProcessor.LoginPathWithError("unknownprovider"));

            var provider = adminAuth.CurrentValue.GetProvider(scheme);
            if (provider is null || !provider.IsConfigured)
                return Results.Redirect(AdminSignInProcessor.LoginPathWithError("unconfigured"));

            // Configured, but the options could not be built from it (for example a client secret
            // that no longer decrypts). Challenging would only fail discovery against the inert
            // placeholder authority and surface as a bare 500.
            var logger = loggers.CreateLogger(typeof(AdminAuthEndpoints));
            if (oidc.Get(scheme).ClientId == ConfigureAdminOidcOptions.InertClientId)
            {
                logger.LogWarning("The {Scheme} sign-in provider is configured but unusable; see earlier errors.", scheme);
                return Results.Redirect(AdminSignInProcessor.LoginPathWithError("provider"));
            }

            // Discovery runs inside the challenge, before the redirect to the provider, so a
            // mistyped Authority throws here rather than reaching OnRemoteFailure.
            try
            {
                await context.ChallengeAsync(
                    scheme, new AuthenticationProperties { RedirectUri = LocalReturnUrl.Sanitize(returnUrl) });
                return Results.Empty;
            }
            catch (Exception ex) when (!context.Response.HasStarted)
            {
                logger.LogWarning(ex, "Could not start a sign-in with {Scheme}.", scheme);
                return Results.Redirect(AdminSignInProcessor.LoginPathWithError("provider"));
            }
        });

        // A returnUrl here means "sign in as someone else, then carry on", which the MCP consent
        // page offers when the wrong account is signed in.
        app.MapGet("/admin/auth/logout", async (HttpContext context, string? returnUrl) =>
        {
            await context.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
            return Results.Redirect(AdminSignInProcessor.LoginPath + LocalReturnUrl.Query("ReturnUrl", returnUrl));
        });

        return app;
    }
}
