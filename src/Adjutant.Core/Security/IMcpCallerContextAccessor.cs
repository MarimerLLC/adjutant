using Adjutant.Core.Models;

namespace Adjutant.Core.Security;

/// <summary>Provides the credential scope for the current MCP call.</summary>
public interface IMcpCallerContextAccessor
{
    McpCallerContext Current { get; }
}

public sealed record McpCallerContext(
    string? UserId,
    string? CredentialId,
    IReadOnlyList<string> AllowedAccounts,
    string OrganizationId = AccountInfo.DefaultOrganizationId)
{
    public static McpCallerContext Unrestricted { get; } = new(null, null, []) { IsUnrestricted = true };

    internal bool IsUnrestricted { get; private init; }

    public bool Owns(string? owner, string organizationId) =>
        string.Equals(OrganizationId, organizationId, StringComparison.Ordinal) &&
        string.Equals(UserId, owner, StringComparison.OrdinalIgnoreCase);

    public bool CanAccessAccount(AccountInfo account) =>
        IsUnrestricted || (account.Owner is null
            ? OrganizationId == AccountInfo.DefaultOrganizationId && account.OrganizationId == AccountInfo.DefaultOrganizationId
            : Owns(account.Owner, account.OrganizationId));

    public bool AllowsAccount(string accountId) =>
        AllowedAccounts.Count == 0 || AllowedAccounts.Contains(accountId, StringComparer.OrdinalIgnoreCase);
}

internal sealed class UnrestrictedMcpCallerContextAccessor : IMcpCallerContextAccessor
{
    public McpCallerContext Current => McpCallerContext.Unrestricted;
}
