using System.Text.Json;
using Adjutant.Core.Models;
using Spectre.Console;

namespace Adjutant.Cli.Commands;

internal static class AccountConfigurationEntry
{
    public static AccountInfo Read(Dictionary<string, JsonElement> account)
    {
        string? ReadString(string pascal, string camel) =>
            account.TryGetValue(pascal, out var value) || account.TryGetValue(camel, out value) ? value.GetString() : null;

        var providerConfig = account.TryGetValue("ProviderConfig", out var config) || account.TryGetValue("providerConfig", out config)
            ? config.Deserialize<Dictionary<string, string>>()
            : null;
        var enabled = !(account.TryGetValue("Enabled", out var value) || account.TryGetValue("enabled", out value)) ||
            value.ValueKind != JsonValueKind.False;
        return new AccountInfo
        {
            Id = ReadString("Id", "id") ?? "",
            DisplayName = ReadString("DisplayName", "displayName") ?? "",
            Provider = ReadString("Provider", "provider") ?? "",
            Owner = ReadString("Owner", "owner"),
            OrganizationId = ReadString("OrganizationId", "organizationId") ?? AccountInfo.DefaultOrganizationId,
            Enabled = enabled,
            ProviderConfig = providerConfig ?? new()
        };
    }

    public static void AddOrReplace(List<Dictionary<string, object>> accounts, string accountId, Dictionary<string, object> account)
    {
        var index = accounts.FindIndex(a =>
            (a.TryGetValue("Id", out var id) || a.TryGetValue("id", out id)) &&
            string.Equals(id?.ToString(), accountId, StringComparison.OrdinalIgnoreCase));

        if (index < 0)
        {
            AnsiConsole.MarkupLine($"[green]Adding new account '{accountId}'...[/]");
            accounts.Add(account);
            return;
        }

        var previous = accounts[index];
        var existingId = (previous.TryGetValue("Id", out var id) ? id : previous["id"]).ToString();
        if (!string.Equals(existingId, accountId, StringComparison.Ordinal))
            throw new InvalidOperationException($"Use the existing account ID '{existingId}' exactly as written.");

        foreach (var property in new[] { "Owner", "owner", "OrganizationId", "organizationId" })
        {
            if (previous.TryGetValue(property, out var value))
                account[property] = value;
        }

        AnsiConsole.MarkupLine($"[yellow]Account '{accountId}' already exists. Updating...[/]");
        accounts[index] = account;
    }
}
