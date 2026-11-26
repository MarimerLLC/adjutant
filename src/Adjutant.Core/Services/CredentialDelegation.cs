using Adjutant.Core.Models;

namespace Adjutant.Core.Services;

/// <summary>
/// Rules for one account using another account's credentials. Today the only case is a JSON
/// calendar on OneDrive that names a Microsoft account in <c>providerConfig.authAccountId</c>:
/// the JSON account (the borrower) reads its files with the named account's (the lender's)
/// token. Every check on whether that is allowed lives in <see cref="CheckLender"/>.
/// </summary>
public static class CredentialDelegation
{
    /// <summary>The providerConfig key a borrowing account uses to name its lender.</summary>
    public const string AuthAccountIdKey = "authAccountId";

    /// <summary>
    /// The ID of the account whose credentials <paramref name="account"/> borrows, or null when
    /// it borrows none. Only a JSON account with <c>source: onedrive</c> borrows; a local JSON
    /// file needs no credentials, so an <c>authAccountId</c> left on one is ignored.
    /// </summary>
    public static string? GetLenderId(AccountInfo account)
    {
        if (!IsJsonProvider(account.Provider))
            return null;

        var source = account.ProviderConfig.GetValueOrDefault("source", "local");
        if (!source.Equals("onedrive", StringComparison.OrdinalIgnoreCase))
            return null;

        return account.ProviderConfig.TryGetValue(AuthAccountIdKey, out var lenderId) &&
               !string.IsNullOrWhiteSpace(lenderId)
            ? lenderId
            : null;
    }

    /// <summary>
    /// The accounts that borrow <paramref name="lender"/>'s credentials. The admin console
    /// lists them before an account is disabled or deleted, and sign-in uses them to decide
    /// whether the lender needs the Files.Read scope.
    /// </summary>
    public static IReadOnlyList<AccountInfo> FindBorrowers(IEnumerable<AccountInfo> accounts, AccountInfo lender) =>
        accounts.Where(a => string.Equals(GetLenderId(a), lender.Id, StringComparison.OrdinalIgnoreCase) &&
                            HasSameOwner(a, lender.Owner, lender.OrganizationId)).ToList();

    public static bool HasSameOwner(AccountInfo account, string? owner, string organizationId) =>
        string.Equals(account.Owner, owner, StringComparison.OrdinalIgnoreCase) &&
        string.Equals(account.OrganizationId, organizationId, StringComparison.Ordinal);

    /// <summary>
    /// Why <paramref name="borrower"/> may not use the credentials of <paramref name="lender"/>
    /// (looked up as <paramref name="lenderId"/>), or null when it may. A disabled lender is
    /// refused: disabling an account switches off its credentials too, not only its own tools.
    /// </summary>
    public static string? CheckLender(AccountInfo borrower, string lenderId, AccountInfo? lender)
    {
        if (lender is null || !HasSameOwner(borrower, lender.Owner, lender.OrganizationId))
            return $"Account '{borrower.Id}' references auth account '{lenderId}' which was not found or is not accessible. " +
                   $"Check the '{AuthAccountIdKey}' in providerConfig.";

        if (!lender.Enabled)
            return $"Account '{borrower.Id}' gets its OneDrive credentials from '{lenderId}', which is disabled. " +
                   $"Re-enable '{lenderId}' or point {AuthAccountIdKey} at another account.";

        return null;
    }

    /// <summary>
    /// Looks up the lender of <paramref name="borrower"/> and returns it, or null when the
    /// borrower borrows nothing. Throws <see cref="ProviderOperationException"/> with the
    /// <see cref="CheckLender"/> reason when the borrowing is not allowed, so a fan-out reports
    /// it as that account's warning and an explicit call reports it as the tool error.
    /// </summary>
    public static async Task<AccountInfo?> ResolveLenderAsync(IAccountRegistry registry, AccountInfo borrower)
    {
        var lenderId = GetLenderId(borrower);
        if (lenderId is null)
            return null;

        var lender = await registry.GetAccountAsync(lenderId);
        var problem = CheckLender(borrower, lenderId, lender);
        if (problem is not null)
            throw new ProviderOperationException(problem);

        return lender;
    }

    private static bool IsJsonProvider(string provider) =>
        provider.Equals("json", StringComparison.OrdinalIgnoreCase) ||
        provider.Equals("json-calendar", StringComparison.OrdinalIgnoreCase);
}
