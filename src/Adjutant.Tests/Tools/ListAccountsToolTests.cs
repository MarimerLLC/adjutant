using System.Text.Json;
using Adjutant.Core.Configuration;
using Adjutant.Core.Models;
using Adjutant.Core.Providers;
using Adjutant.Core.Services;
using Adjutant.Core.Tools;
using Adjutant.Tests.Helpers;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol;
using Rocks;

namespace Adjutant.Tests.Tools;

[TestClass]
public class ListAccountsToolTests
{
    [TestMethod]
    public async Task ListAccounts_ReturnsAllAccounts()
    {
        var accounts = new List<AccountInfo>
        {
            TestData.CreateAccount(id: "acc-1", displayName: "Account 1"),
            TestData.CreateAccount(id: "acc-2", displayName: "Account 2")
        };

        var registryExpectations = new IAccountRegistryCreateExpectations();
        registryExpectations.Setups.GetEnabledAccounts().ReturnValue(accounts);

        var registry = registryExpectations.Instance();
        var tool = new ListAccountsTool(registry, NullLogger<ListAccountsTool>.Instance);

        var result = await tool.ListAccounts();
        var doc = JsonDocument.Parse(result);
        var accountsArray = doc.RootElement.GetProperty("accounts");

        Assert.AreEqual(2, accountsArray.GetArrayLength());
        Assert.AreEqual("acc-1", accountsArray[0].GetProperty("accountId").GetString());
        Assert.AreEqual("acc-2", accountsArray[1].GetProperty("accountId").GetString());

        registryExpectations.Verify();
    }

    [TestMethod]
    public async Task ListAccounts_DisabledAccount_IsNotListed()
    {
        // Real registry: the enabled filter lives there, not in the tool.
        using var registry = new AccountRegistry(
            new TestOptionsMonitor<AdjutantConfiguration>(new AdjutantConfiguration
            {
                Accounts =
                [
                    TestData.CreateAccount(id: "acc-on", displayName: "On"),
                    TestData.CreateAccount(id: "acc-off", displayName: "Off", enabled: false)
                ]
            }),
            NullLogger<AccountRegistry>.Instance);
        var tool = new ListAccountsTool(registry, NullLogger<ListAccountsTool>.Instance);

        using var doc = JsonDocument.Parse(await tool.ListAccounts());
        var accountsArray = doc.RootElement.GetProperty("accounts");

        Assert.AreEqual(1, accountsArray.GetArrayLength());
        Assert.AreEqual("acc-on", accountsArray[0].GetProperty("accountId").GetString());
        Assert.IsTrue(accountsArray[0].GetProperty("enabled").GetBoolean());
    }

    [TestMethod]
    public async Task ListAccounts_IncludeDisabled_ListsDisabledAccountMarkedAsSuch()
    {
        using var registry = new AccountRegistry(
            new TestOptionsMonitor<AdjutantConfiguration>(new AdjutantConfiguration
            {
                Accounts =
                [
                    TestData.CreateAccount(id: "acc-on", displayName: "On"),
                    TestData.CreateAccount(id: "acc-off", displayName: "Off", enabled: false)
                ]
            }),
            NullLogger<AccountRegistry>.Instance);
        var tool = new ListAccountsTool(registry, NullLogger<ListAccountsTool>.Instance);

        using var doc = JsonDocument.Parse(await tool.ListAccounts(includeDisabled: true));
        var byId = doc.RootElement.GetProperty("accounts").EnumerateArray()
            .ToDictionary(a => a.GetProperty("accountId").GetString()!, a => a.GetProperty("enabled").GetBoolean());

        Assert.AreEqual(2, byId.Count);
        Assert.IsTrue(byId["acc-on"]);
        Assert.IsFalse(byId["acc-off"]);
    }

    [TestMethod]
    public async Task ListAccounts_EmptyAccounts_ReturnsEmptyArray()
    {
        var registryExpectations = new IAccountRegistryCreateExpectations();
        registryExpectations.Setups.GetEnabledAccounts().ReturnValue([]);

        var registry = registryExpectations.Instance();
        var tool = new ListAccountsTool(registry, NullLogger<ListAccountsTool>.Instance);

        var result = await tool.ListAccounts();
        var doc = JsonDocument.Parse(result);
        var accountsArray = doc.RootElement.GetProperty("accounts");

        Assert.AreEqual(0, accountsArray.GetArrayLength());

        registryExpectations.Verify();
    }

    [TestMethod]
    public async Task ListAccounts_Exception_ThrowsGenericMcpException()
    {
        var registryExpectations = new IAccountRegistryCreateExpectations();
        registryExpectations.Setups.GetEnabledAccounts()
            .Callback(() => throw new InvalidOperationException("Sensitive registry detail"));

        var registry = registryExpectations.Instance();
        var tool = new ListAccountsTool(registry, NullLogger<ListAccountsTool>.Instance);

        var ex = await Assert.ThrowsExactlyAsync<McpException>(() => tool.ListAccounts());
        Assert.AreEqual("Failed to list accounts.", ex.Message);
        Assert.IsFalse(ex.Message.Contains("Sensitive registry detail"));

        registryExpectations.Verify();
    }
}
