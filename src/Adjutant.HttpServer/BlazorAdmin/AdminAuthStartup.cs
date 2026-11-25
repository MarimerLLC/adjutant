using Adjutant.Core.Configuration;
using Adjutant.Core.Security;
using Microsoft.Extensions.Options;

namespace Adjutant.HttpServer.BlazorAdmin;

/// <summary>
/// Startup-time reporting and claim-code issuance for admin console sign-in.
/// </summary>
public static class AdminAuthStartup
{
    /// <summary>
    /// Issues a claim code when no allow-list exists, and logs what the login page will offer,
    /// so an operator can tell from the startup log alone how to get in.
    /// </summary>
    public static void ConfigureAdminAuth(this WebApplication app)
    {
        var config = app.Services.GetRequiredService<IOptionsMonitor<AdminAuthConfiguration>>().CurrentValue;
        var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Adjutant.AdminAuth");

        EnsureAdminToken(app.Services.GetRequiredService<IAdminTokenStore>(), logger);

        var providers = config.ConfiguredProviderSchemes().ToList();
        if (providers.Count > 0)
            logger.LogInformation("Admin console sign-in providers: {Providers}", string.Join(", ", providers));
        else
            logger.LogInformation("No admin console sign-in providers are configured.");

        var users = app.Services.GetRequiredService<IAdminUserStore>();
        if (config.IsTokenLoginAllowed(users))
        {
            logger.LogWarning(
                "Admin token login is enabled on the console login page. It is the break-glass path " +
                "and resolves off automatically after the first successful sign-in with a provider; " +
                "set AdminAuth:AllowTokenLogin explicitly to override.");
        }

        if (config.AllowedEmails.Count > 0)
        {
            logger.LogInformation(
                "Admin console allow-list has {Count} entr(ies).", config.AllowedEmails.Count);
            return;
        }

        // Issued whenever the allow-list is empty, even with no provider configured yet: a
        // provider can be added later without a restart, and the code must already exist when
        // that first sign-in arrives. On its own it grants nothing — it is only accepted
        // alongside an identity a provider has already verified.
        var claimCode = app.Services.GetRequiredService<IAdminClaimCodeService>();
        var code = claimCode.Issue();

        logger.LogWarning("========================================================================");
        logger.LogWarning("No admin console allow-list is configured yet.");
        logger.LogWarning("Sign in with a provider, then enter this code to claim the server:");
        logger.LogWarning("    Claim code: {Code}", code);
        logger.LogWarning("It is single-use, also written to {File} in the data directory,", AdminClaimCodeService.FileName);
        logger.LogWarning("and a new one is issued each time the server starts unclaimed.");
        logger.LogWarning("========================================================================");
    }

    /// <summary>
    /// Makes sure the admin API and console token login have a token before the server listens,
    /// generating one on a first start the way <c>ConfigureMcpProtection</c> does for the MCP key.
    /// </summary>
    private static void EnsureAdminToken(IAdminTokenStore tokens, ILogger logger)
    {
        if (tokens.IsConfigured)
        {
            logger.LogInformation("Admin token: configured (ADJUTANT_ADMIN_TOKEN or Adjutant:AdminToken).");
            return;
        }

        var secret = tokens.EnsureToken();
        if (secret is null)
        {
            logger.LogInformation(
                "Admin token: the one generated at first start ({File} in the data directory). To replace it, " +
                "set ADJUTANT_ADMIN_TOKEN, or delete {File} and restart for a new one.",
                AdminTokenStore.FileName, AdminTokenStore.FileName);
            return;
        }

        logger.LogWarning("========================================================================");
        logger.LogWarning("No admin token was configured, so one has been generated for you.");
        logger.LogWarning("Copy it now - it is hashed at rest and will never be shown again.");
        logger.LogWarning("    Admin token: {Secret}", secret);
        logger.LogWarning("Use it to sign in to the admin console (/admin/ui) and as");
        logger.LogWarning("'Authorization: Bearer <token>' on the admin API (/admin).");
        logger.LogWarning("Lost it? Delete {File} from the data directory and restart,", AdminTokenStore.FileName);
        logger.LogWarning("or set ADJUTANT_ADMIN_TOKEN, which always takes precedence.");
        logger.LogWarning("========================================================================");
    }
}
