using Adjutant.Core.Configuration;
using Adjutant.Core.Security;
using Adjutant.Core.Services;
using Microsoft.Extensions.Options;

namespace Adjutant.HttpServer.BlazorAdmin;

/// <summary>
/// Tells someone who has just started the server what to do next, while no accounts exist yet.
/// The startup log already holds the MCP key and claim-code banners and the dashboard has an
/// empty state; this connects them into steps, using the server's real address and the sign-in
/// options actually in effect.
/// </summary>
public static class FirstRunGuide
{
    /// <summary>Where the steps are written up at more length.</summary>
    public const string DocsUrl = "https://github.com/MarimerLLC/adjutant/blob/main/docs/INSTALLATION.md#first-run-http-server";

    /// <summary>What the steps depend on, gathered from the running server.</summary>
    public sealed record State(
        string BaseUrl,
        IReadOnlyList<string> Providers,
        bool TokenLoginAllowed,
        bool AdminTokenConfigured,
        bool ClaimNeeded,
        bool McpKeyRequired);

    /// <summary>
    /// Logs the steps after the server has started, on every start while there are no accounts
    /// (setup may span restarts), and nothing once one exists. Resolving the account registry here
    /// also puts its own "no accounts" warning in the startup log rather than on the first tool call.
    /// </summary>
    public static void LogFirstRunGuide(this WebApplication app)
    {
        var registry = app.Services.GetRequiredService<IAccountRegistry>();
        if (registry.GetAllAccountsAsync().GetAwaiter().GetResult().Any())
            return;

        var adminAuth = app.Services.GetRequiredService<IOptionsMonitor<AdminAuthConfiguration>>().CurrentValue;
        var server = app.Services.GetRequiredService<IOptionsMonitor<AdjutantConfiguration>>().CurrentValue;
        var state = new State(
            BaseUrl: ResolveBaseUrl(server.ExternalBaseUrl, app.Urls),
            Providers: adminAuth.ConfiguredProviderSchemes()
                .Select(scheme => AdminOidc.DisplayName(scheme, adminAuth))
                .ToList(),
            TokenLoginAllowed: adminAuth.IsTokenLoginAllowed(app.Services.GetRequiredService<IAdminUserStore>()),
            AdminTokenConfigured: app.Services.GetRequiredService<IAdminTokenStore>().IsConfigured,
            ClaimNeeded: adminAuth.AllowedEmails.Count == 0,
            McpKeyRequired: server.Mcp.RequireApiKey);

        var logger = app.Services.GetRequiredService<ILoggerFactory>().CreateLogger("Adjutant.FirstRun");
        foreach (var line in BuildLines(state))
            logger.LogWarning("{Line}", line);
    }

    /// <summary>The banner, one log line per entry.</summary>
    public static IReadOnlyList<string> BuildLines(State state)
    {
        const string rule = "========================================================================";
        var lines = new List<string>
        {
            rule,
            "Adjutant is running, but no accounts are configured yet. Next steps:",
            $"  1. Open {state.BaseUrl}/admin/ui and {SignInStep(state)}",
            $"  2. Add an email/calendar account ({state.BaseUrl}/admin/ui/accounts/add).",
            $"  3. Point your MCP client at {state.BaseUrl}/ {McpStep(state)}",
            $"Docs: {DocsUrl}",
            rule
        };
        return lines;
    }

    private static string SignInStep(State state)
    {
        var token = state.AdminTokenConfigured
            ? "the configured admin token (ADJUTANT_ADMIN_TOKEN or Adjutant:AdminToken)"
            : $"the admin token from the first-start banner (lost it? delete {AdminTokenStore.FileName} and restart)";
        var providers = state.Providers.Count > 0 ? string.Join(" or ", state.Providers) : null;
        var claim = state.ClaimNeeded ? ", then enter the claim code above" : "";

        return (state.TokenLoginAllowed, providers) switch
        {
            (true, null) => $"sign in with {token}.",
            (true, not null) => $"sign in with {token}, or with {providers}{claim}.",
            (false, not null) => $"sign in with {providers}{claim}.",
            _ => "sign in - but no sign-in method is enabled: set AdminAuth:Providers, or " +
                 "AdminAuth:AllowTokenLogin to true, in appsettings.json and restart."
        };
    }

    private static string McpStep(State state) => state.McpKeyRequired
        ? "with an MCP API key (from the first-start banner, ADJUTANT_MCP_KEY, or MCP Access in the console)."
        : "(no API key needed: Adjutant:Mcp:RequireApiKey is false).";

    /// <summary>
    /// <c>Adjutant:ExternalBaseUrl</c> when set, as the address clients actually use; otherwise
    /// the first address the server listens on, with a wildcard host (<c>+</c>, <c>*</c>,
    /// <c>0.0.0.0</c>, <c>[::]</c>) shown as <c>&lt;host&gt;</c>, because from inside a container
    /// the server can't know the name it is reached by.
    /// </summary>
    public static string ResolveBaseUrl(string? externalBaseUrl, IEnumerable<string> listenUrls)
    {
        // http(s) only: on Linux "/adjutant" parses as an absolute file:// URI.
        if (!string.IsNullOrWhiteSpace(externalBaseUrl) &&
            Uri.TryCreate(externalBaseUrl.Trim(), UriKind.Absolute, out var external) &&
            (external.Scheme == Uri.UriSchemeHttp || external.Scheme == Uri.UriSchemeHttps))
        {
            return externalBaseUrl.Trim().TrimEnd('/');
        }

        var url = listenUrls.FirstOrDefault(u => u.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
            ?? listenUrls.FirstOrDefault();
        if (string.IsNullOrEmpty(url))
            return "http://<host>:8080";

        foreach (var wildcard in new[] { "://+:", "://*:", "://0.0.0.0:", "://[::]:" })
        {
            var at = url.IndexOf(wildcard, StringComparison.Ordinal);
            if (at >= 0)
                return string.Concat(url.AsSpan(0, at), "://<host>:", url.AsSpan(at + wildcard.Length)).TrimEnd('/');
        }

        return url.TrimEnd('/');
    }
}
