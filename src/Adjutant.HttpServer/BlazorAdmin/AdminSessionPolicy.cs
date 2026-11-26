using System.Security.Claims;
using Adjutant.Core.Configuration;
using Adjutant.Core.Security;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;

namespace Adjutant.HttpServer.BlazorAdmin;

/// <summary>
/// Decides whether an already-established console session is still authorized.
///
/// Separate from the authentication state provider so the rule can be exercised directly: this
/// is what makes removing an administrator take effect, and a silent regression here would not
/// show up as a failure anywhere else.
/// </summary>
public static class AdminSessionPolicy
{
    /// <summary>Value of the provider claim for a session established with the admin token.</summary>
    public const string TokenProvider = "token";

    internal static string? GetOwner(ClaimsPrincipal? user) =>
        user?.Identity?.IsAuthenticated == true &&
        user.FindFirst(AdminOidc.ProviderClaimType)?.Value != TokenProvider
            ? AdminEmailAllowList.Normalize(user.FindFirstValue(ClaimTypes.Email))
            : null;

    /// <summary>
    /// Claim carrying <see cref="IAdminTokenStore.Fingerprint"/> of the token a token session was
    /// established with.
    /// </summary>
    public const string TokenFingerprintClaimType = "adjutant:admin_token";

    /// <summary>Why a session was ended, for logging. <see cref="Reason.None"/> means it stands.</summary>
    public enum Reason
    {
        None,
        NotAuthenticated,
        TokenLoginDisabled,
        TokenChanged,
        NotOnAllowList,
        SubjectChanged
    }

    /// <param name="tokens">
    /// When given, a token session must also carry the fingerprint of the admin token now in
    /// effect, so replacing the token ends the sessions opened with the old one. That includes
    /// sessions from before 2.1.x, when a server without a token accepted any input. Null only
    /// where the principal is not a console cookie (an MCP OAuth token's owner, re-shaped).
    /// </param>
    public static Reason Evaluate(
        ClaimsPrincipal? user,
        AdminAuthConfiguration config,
        IAdminUserStore users,
        IAdminTokenStore? tokens = null)
    {
        if (user?.Identity?.IsAuthenticated != true)
            return Reason.NotAuthenticated;

        // A token session is valid exactly as long as token login is. The first successful
        // provider sign-in flips AllowTokenLogin off, which should also end sessions established
        // that way rather than leaving a break-glass login open for the rest of the cookie's life.
        if (user.FindFirst(AdminOidc.ProviderClaimType)?.Value == TokenProvider)
        {
            if (!config.IsTokenLoginAllowed(users))
                return Reason.TokenLoginDisabled;

            return tokens is null || IsBoundToCurrentToken(user, tokens) ? Reason.None : Reason.TokenChanged;
        }

        var email = user.FindFirst(ClaimTypes.Email)?.Value;
        if (!AdminEmailAllowList.IsAllowed(email, config.AllowedEmails))
            return Reason.NotOnAllowList;

        // The bound subject can change by removing and re-adding a user record, which should
        // end the session too — otherwise a live circuit outlives the binding it rests on.
        var subject = user.FindFirst(ClaimTypes.NameIdentifier)?.Value;
        if (!string.IsNullOrEmpty(subject))
        {
            var known = users.Find(email);
            if (known is not null && !string.IsNullOrEmpty(known.Subject) &&
                !string.Equals(known.Subject, subject, StringComparison.Ordinal))
            {
                return Reason.SubjectChanged;
            }
        }

        return Reason.None;
    }

    /// <summary>True when a token session carries the fingerprint of the token now in effect.</summary>
    public static bool IsBoundToCurrentToken(ClaimsPrincipal user, IAdminTokenStore tokens)
    {
        var current = tokens.Fingerprint;
        var presented = user.FindFirst(TokenFingerprintClaimType)?.Value;
        return current is not null && presented is not null &&
               System.Security.Cryptography.CryptographicOperations.FixedTimeEquals(
                   System.Text.Encoding.UTF8.GetBytes(presented), System.Text.Encoding.UTF8.GetBytes(current));
    }

    /// <summary>
    /// Cookie validation for every request, not only circuit revalidation: a token session that
    /// no longer passes <see cref="Evaluate"/> is rejected and its cookie removed before the
    /// request reaches the console, the admin API's cookie paths or the OAuth consent page.
    /// Provider sessions are left to the circuit's periodic revalidation, as before.
    /// </summary>
    public static async Task ValidateTokenSessionAsync(CookieValidatePrincipalContext context)
    {
        var user = context.Principal;
        if (user?.FindFirst(AdminOidc.ProviderClaimType)?.Value != TokenProvider)
            return;

        var services = context.HttpContext.RequestServices;
        var reason = Evaluate(
            user,
            services.GetRequiredService<Microsoft.Extensions.Options.IOptionsMonitor<AdminAuthConfiguration>>().CurrentValue,
            services.GetRequiredService<IAdminUserStore>(),
            services.GetRequiredService<IAdminTokenStore>());
        if (reason == Reason.None)
            return;

        services.GetRequiredService<ILoggerFactory>().CreateLogger("Adjutant.AdminAuth")
            .LogInformation("Ending an admin-token console session: {Reason}", reason);
        context.RejectPrincipal();
        await context.HttpContext.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    }
}
