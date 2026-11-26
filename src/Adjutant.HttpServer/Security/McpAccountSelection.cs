using Adjutant.Core.Models;
using Adjutant.Core.Security;
using Adjutant.Core.Services;

namespace Adjutant.HttpServer.Security;

internal static class McpAccountSelection
{
    public const string ModeField = "accountAccess";
    public const string AccountField = "allowedAccounts";
    public const string All = "all";
    public const string Selected = "selected";
    public const string ClaimType = "mcp:allowed_account";

    public static async Task<IEnumerable<AccountInfo>> ListAsync(IAccountRegistry registry, string? owner)
    {
        var caller = new McpCallerContext(owner, null, []);
        return (await registry.GetAllAccountsAsync()).Where(caller.CanAccessAccount);
    }

    public static async Task<string[]> ResolveAsync(IAccountRegistry registry, string? owner, string mode, IEnumerable<string> selection)
    {
        var ids = selection.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (mode == All)
            return [];
        if (mode != Selected || ids.Length == 0)
            throw new ArgumentException("Choose all accounts or select at least one account.");

        var accounts = (await ListAsync(registry, owner)).ToDictionary(a => a.Id, StringComparer.OrdinalIgnoreCase);
        if (ids.Any(id => string.IsNullOrWhiteSpace(id) || !accounts.ContainsKey(id)))
            throw new ArgumentException("The account selection is no longer valid. Check the available accounts and try again.");

        return ids.Select(id => accounts[id].Id).ToArray();
    }
}
