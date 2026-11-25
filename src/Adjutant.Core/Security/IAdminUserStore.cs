namespace Adjutant.Core.Security;

/// <summary>
/// Tracks the identities that have signed in to the admin console.
/// </summary>
public interface IAdminUserStore
{
    /// <summary>All known users, ordered by email.</summary>
    IReadOnlyList<AdminUser> List();

    /// <summary>
    /// True once an OIDC admin sign-in has been recorded through one of <paramref name="providers"/>.
    /// Records are written only after the allow-list check or the claim code has passed, so this
    /// means one of those providers is known to work end to end, which is when the break-glass
    /// token login can safely turn itself off. Pass the currently configured schemes: a sign-in
    /// through a provider that has since been removed proves nothing about the ones left.
    /// </summary>
    bool HasRecordedSignIn(IEnumerable<string> providers);

    /// <summary>Looks up a user by email, or null when the address is unknown.</summary>
    AdminUser? Find(string? email);

    /// <summary>
    /// Records a successful provider authentication and enforces subject binding. A
    /// <see cref="AdminSignInResult.SubjectMismatch"/> result means the sign-in must be refused.
    /// </summary>
    AdminSignInResult RecordSignIn(string email, string provider, string? subject);

    /// <summary>Forgets a user, unbinding their subject. Returns false when unknown.</summary>
    bool Remove(string email);
}
