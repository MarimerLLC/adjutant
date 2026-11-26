using Adjutant.Core.Models;
using Adjutant.Core.Security;
using Adjutant.Core.Services;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;

namespace Adjutant.Core.Tools;

// Only tools use this view. The admin console and providers use the underlying registry.
internal sealed class McpAccountRegistry(
    IAccountRegistry registry,
    IMcpCallerContextAccessor callers,
    ILogger<McpAccountRegistry> logger) : IAccountRegistry
{
    public const string ServiceKey = "mcp";

    public async Task<AccountInfo?> GetAccountAsync(string accountId)
    {
        var caller = callers.Current;
        var account = await registry.GetAccountAsync(accountId);
        if (account is null)
            return null;

        if (!caller.CanAccessAccount(account))
        {
            logger.LogWarning(
                "MCP credential {CredentialId} for user {UserId} cannot access account {AccountId}",
                caller.CredentialId, caller.UserId, accountId);
            return null;
        }

        if (!caller.AllowsAccount(accountId))
        {
            logger.LogWarning(
                "MCP credential {CredentialId} for user {UserId} was denied access to account {AccountId}",
                caller.CredentialId, caller.UserId, accountId);
            throw new McpAccountAccessException(accountId);
        }

        return account;
    }

    public async Task<IEnumerable<AccountInfo>> GetAllAccountsAsync() => Filter(await registry.GetAllAccountsAsync());

    public IEnumerable<AccountInfo> GetEnabledAccounts() => Filter(registry.GetEnabledAccounts());

    public IEnumerable<AccountInfo> GetAccountsByProvider(string provider) => Filter(registry.GetAccountsByProvider(provider));

    public IEnumerable<AccountInfo> GetAccountsByDomain(string domain) => Filter(registry.GetAccountsByDomain(domain));

    private IEnumerable<AccountInfo> Filter(IEnumerable<AccountInfo> accounts)
    {
        var caller = callers.Current;
        return accounts.Where(account => caller.CanAccessAccount(account) && caller.AllowsAccount(account.Id));
    }
}

internal sealed class McpAccountAccessException(string accountId)
    : McpException($"This credential is not authorized for account '{accountId}'.");
