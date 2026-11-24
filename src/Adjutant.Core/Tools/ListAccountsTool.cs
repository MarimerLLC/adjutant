using System.ComponentModel;
using System.Text.Json;
using Adjutant.Core.Models;
using Adjutant.Core.Services;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace Adjutant.Core.Tools;

/// <summary>
/// MCP tool for listing the enabled accounts
/// </summary>
[McpServerToolType]
public sealed class ListAccountsTool(
    IAccountRegistry accountRegistry,
    ILogger<ListAccountsTool> logger)
{
    [McpServerTool, Description("List the enabled accounts with their capabilities and permissions. Disabled accounts are left out unless includeDisabled is true; they are never queried by tools that fan out over all accounts. Returns accountId, provider, displayName, domains, enabled, capabilities (calendar, email, contacts), and permissions (emailRead, emailSend, calendarRead, calendarWrite, contactsRead, contactsWrite) for each. Permissions are what the account actually allows — a false value means every tool needing it will be refused for that account, so pick an account whose permissions cover the operation. Use the accountId values when calling other tools to scope operations to a specific account.")]
    public async Task<string> ListAccounts(
        [Description("Also list disabled accounts (default false), e.g. to explain why an account the user expects is missing. Each entry's enabled field tells them apart.")] bool includeDisabled = false)
    {
        logger.LogInformation("Listing accounts: includeDisabled={IncludeDisabled}", includeDisabled);

        try
        {
            // Enabled accounts by default: a disabled account is not part of any fan-out, so
            // listing it unasked would invite the caller to pick an account that is switched off.
            var accounts = includeDisabled
                ? await accountRegistry.GetAllAccountsAsync()
                : accountRegistry.GetEnabledAccounts();

            var response = new
            {
                accounts = accounts.Select(a => new
                {
                    accountId = a.Id,
                    provider = a.Provider,
                    displayName = a.DisplayName,
                    domains = a.Domains,
                    enabled = a.Enabled,
                    capabilities = AccountCapabilities.GetCapabilities(a)
                        .Select(c => new { name = c.Name, readOnly = c.ReadOnly }),
                    permissions = Describe(AccountCapabilities.GetEffectivePermissions(a))
                })
            };

            return JsonSerializer.Serialize(response, new JsonSerializerOptions
            {
                WriteIndented = true
            });
        }
        catch (Exception ex) when (ex is not McpException)
        {
            logger.LogError(ex, "Error listing accounts");
            throw ToolGuard.Failure("list accounts", ex);
        }
    }

    /// <summary>
    /// Projects effective permissions into the camelCase shape the tool documents.
    /// </summary>
    private static object Describe(AccountPermissions permissions) => new
    {
        emailRead = permissions.EmailRead,
        emailSend = permissions.EmailSend,
        calendarRead = permissions.CalendarRead,
        calendarWrite = permissions.CalendarWrite,
        contactsRead = permissions.ContactsRead,
        contactsWrite = permissions.ContactsWrite
    };
}
