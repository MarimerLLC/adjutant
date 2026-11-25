namespace Adjutant.Core.Security;

/// <summary>
/// The break-glass admin token: the credential for the admin REST API and, while token login is
/// allowed, for the console login page.
///
/// A token configured by the operator (<c>ADJUTANT_ADMIN_TOKEN</c> or <c>Adjutant:AdminToken</c>)
/// always wins. Without one, the server generates a token at first start and keeps only its hash,
/// the way it does for the first MCP API key, so a fresh install is never left open.
/// </summary>
public interface IAdminTokenStore
{
    /// <summary>True when the operator configured a token, rather than relying on a generated one.</summary>
    bool IsConfigured { get; }

    /// <summary>True when any token, configured or generated, can be presented.</summary>
    bool HasToken { get; }

    /// <summary>
    /// Generates and stores a token when there is neither a configured nor a stored one.
    /// Returns the new secret, to be shown once, or null when a token already existed.
    /// </summary>
    string? EnsureToken();

    /// <summary>Fixed-time check of a presented token. False when no token exists.</summary>
    bool Matches(string? presented);

    /// <summary>
    /// A value derived from the token now in effect, stored in a console session established
    /// with it. It changes whenever the token does, which is how replacing the token ends the
    /// sessions opened with the old one. Null when no token exists. It reveals nothing usable
    /// about the token itself.
    /// </summary>
    string? Fingerprint { get; }
}
