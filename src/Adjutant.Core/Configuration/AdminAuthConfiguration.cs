using Adjutant.Core.Security;

namespace Adjutant.Core.Configuration;

/// <summary>
/// Sign-in configuration for the Blazor admin console, bound from the top-level
/// <c>AdminAuth</c> section (a sibling of <c>Adjutant</c>, not a child of it, so the
/// server's own identity settings stay separate from the mailbox accounts it serves).
/// </summary>
public class AdminAuthConfiguration
{
    /// <summary>
    /// Email addresses permitted to sign in. An entry beginning with <c>@</c> matches a whole
    /// domain. An empty list means nobody is allowed yet and the first successful sign-in must
    /// go through the claim flow.
    /// </summary>
    public List<string> AllowedEmails { get; set; } = [];

    /// <summary>
    /// Whether the admin token may be used as a password on the console login page.
    ///
    /// Null (the default) resolves to true until the server has recorded a successful OIDC admin
    /// sign-in, and false from then on. Configuring a provider is not enough: a provider saved
    /// with a mistake would otherwise remove the only way back into the console to fix it. Set
    /// explicitly to override — <c>true</c> to keep it after a provider sign-in has worked,
    /// <c>false</c> to remove it before one has.
    /// </summary>
    public bool? AllowTokenLogin { get; set; }

    /// <summary>
    /// OIDC providers keyed by scheme name (<c>google</c>, <c>microsoft</c>). A provider whose
    /// entry is missing or incomplete is treated as unconfigured and offered to nobody.
    /// </summary>
    public Dictionary<string, AdminAuthProviderConfiguration> Providers { get; set; } =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Returns the provider entry for a scheme, or null when absent.</summary>
    public AdminAuthProviderConfiguration? GetProvider(string scheme) =>
        Providers.TryGetValue(scheme, out var provider) ? provider : null;

    /// <summary>Scheme names that are fully configured and can be offered on the login page.</summary>
    public IEnumerable<string> ConfiguredProviderSchemes() =>
        Providers.Where(p => p.Value.IsConfigured).Select(p => p.Key);

    /// <summary>
    /// Resolves the tri-state <see cref="AllowTokenLogin"/>. Automatic resolution keeps the token
    /// while no provider is configured (a fresh server, or every provider removed later) and
    /// while none of the configured ones has completed a sign-in yet.
    /// </summary>
    /// <param name="oidcSignInRecorded">
    /// Whether anyone has completed an OIDC admin sign-in, through a provider that is still
    /// configured, that passed the allow-list or the claim code. See
    /// <see cref="IsTokenLoginAllowed(IAdminUserStore)"/>.
    /// </param>
    public bool IsTokenLoginAllowed(bool oidcSignInRecorded) =>
        AllowTokenLogin ?? (!oidcSignInRecorded || !ConfiguredProviderSchemes().Any());

    /// <summary>
    /// <see cref="IsTokenLoginAllowed(bool)"/> with the sign-in history read from
    /// <paramref name="users"/>, counting only sign-ins through currently configured providers.
    /// </summary>
    public bool IsTokenLoginAllowed(IAdminUserStore users) =>
        IsTokenLoginAllowed(users.HasRecordedSignIn(ConfiguredProviderSchemes()));

    /// <summary>
    /// Whether the console login page may offer the token form. When no admin token is
    /// configured, the form accepts any input. Outside Development that is only acceptable
    /// before any provider is configured, so automatic resolution keeps the 2.0.0 rule there:
    /// configuring a provider closes it. An explicit <see cref="AllowTokenLogin"/> still wins.
    /// </summary>
    public bool IsTokenFormOffered(bool oidcSignInRecorded, bool adminTokenConfigured, bool development)
    {
        if (AllowTokenLogin is null && !adminTokenConfigured && !development && ConfiguredProviderSchemes().Any())
            return false;

        return IsTokenLoginAllowed(oidcSignInRecorded);
    }
}

/// <summary>
/// One OIDC provider's client registration. The operator creates this in their own Google
/// Cloud or Entra tenant; nothing is shipped with the product.
/// </summary>
public class AdminAuthProviderConfiguration
{
    /// <summary>
    /// OIDC issuer, e.g. <c>https://accounts.google.com</c> or
    /// <c>https://login.microsoftonline.com/common/v2.0</c>. Discovery does the rest.
    /// </summary>
    public string? Authority { get; set; }

    public string? ClientId { get; set; }

    public string? ClientSecret { get; set; }

    /// <summary>Human-readable name for the login button. Falls back to the scheme name.</summary>
    public string? DisplayName { get; set; }

    /// <summary>
    /// True only when every value needed to complete a redirect flow is present. Partial
    /// configuration is treated as absent so a half-finished setup fails closed rather than
    /// producing an opaque error at the provider.
    /// </summary>
    public bool IsConfigured =>
        !string.IsNullOrWhiteSpace(Authority) &&
        !string.IsNullOrWhiteSpace(ClientId) &&
        !string.IsNullOrWhiteSpace(ClientSecret);
}
