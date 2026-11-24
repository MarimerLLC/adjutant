using System.Text.Json;
using Adjutant.Core.Models;
using Adjutant.Core.Services;
using Adjutant.Core.Tools;
using Adjutant.Tests.Helpers;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol;
using Rocks;

namespace Adjutant.Tests.Tools;

[TestClass]
public class CreateContactToolTests
{
    [TestMethod]
    [DataRow("")]
    [DataRow("   ")]
    public async Task CreateContact_BlankAddressBookName_IsTreatedAsOmitted(string blank)
    {
        var account = TestData.CreateAccount(id: "acc-1", provider: "microsoft365");

        var regExp = new IAccountRegistryCreateExpectations();
        regExp.Setups.GetEnabledAccounts().ReturnValue([account]);

        var provExp = new IProviderServiceCreateExpectations();
        provExp.Setups.CreateContactAsync(
            "acc-1", "Alice", Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<List<string>?>(), Arg.Any<List<string>?>(),
            Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .ReturnValue(Task.FromResult("contact-1"));

        var factExp = new IProviderServiceFactoryCreateExpectations();
        factExp.Setups.GetProvider("microsoft365").ReturnValue(provExp.Instance());

        var tool = new CreateContactTool(regExp.Instance(), factExp.Instance(), NullLogger<CreateContactTool>.Instance);

        // Some clients send "" for every optional parameter; that must not turn on book selection.
        var result = await tool.CreateContact("Alice", accountId: "", addressBookName: blank);

        Assert.AreEqual("contact-1", JsonDocument.Parse(result).RootElement.GetProperty("contactId").GetString());
        regExp.Verify();
        factExp.Verify();
        provExp.Verify();
    }

    [TestMethod]
    public async Task CreateContact_AddressBookNameWithoutAccountId_ThrowsBeforeAnyLookup()
    {
        var regExp = new IAccountRegistryCreateExpectations();
        var factExp = new IProviderServiceFactoryCreateExpectations();
        var tool = new CreateContactTool(regExp.Instance(), factExp.Instance(), NullLogger<CreateContactTool>.Instance);

        var ex = await Assert.ThrowsExactlyAsync<McpException>(
            () => tool.CreateContact("Alice", addressBookName: "Family"));

        StringAssert.Contains(ex.Message, "explicit accountId");
        regExp.Verify();
        factExp.Verify();
    }

    [TestMethod]
    public async Task CreateContact_AddressBookNameOnNonCardDavAccount_Throws()
    {
        var account = TestData.CreateAccount(id: "acc-1", provider: "microsoft365");

        var regExp = new IAccountRegistryCreateExpectations();
        regExp.Setups.GetAccountAsync("acc-1").ReturnValue(Task.FromResult<AccountInfo?>(account));

        var provExp = new IProviderServiceCreateExpectations();
        var factExp = new IProviderServiceFactoryCreateExpectations();
        factExp.Setups.GetProvider("microsoft365").ReturnValue(provExp.Instance());

        var tool = new CreateContactTool(regExp.Instance(), factExp.Instance(), NullLogger<CreateContactTool>.Instance);

        var ex = await Assert.ThrowsExactlyAsync<McpException>(
            () => tool.CreateContact("Alice", accountId: "acc-1", addressBookName: "Family"));

        StringAssert.Contains(ex.Message, "only for CardDAV");
        regExp.Verify();
        factExp.Verify();
        provExp.Verify();
    }

    [TestMethod]
    public async Task CreateContact_AddressBookNameOnCardDavAccount_PassesTrimmedName()
    {
        var account = TestData.CreateAccount(id: "dav-1", provider: "dav");

        var regExp = new IAccountRegistryCreateExpectations();
        regExp.Setups.GetAccountAsync("dav-1").ReturnValue(Task.FromResult<AccountInfo?>(account));

        var davExp = new IDavProviderServiceCreateExpectations();
        davExp.Setups.CreateContactInAddressBookAsync(
            "dav-1", "Alice", "Family", Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<List<string>?>(), Arg.Any<List<string>?>(),
            Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .ReturnValue(Task.FromResult("contact-2"));

        var factExp = new IProviderServiceFactoryCreateExpectations();
        factExp.Setups.GetProvider("dav").ReturnValue(davExp.Instance());

        var tool = new CreateContactTool(regExp.Instance(), factExp.Instance(), NullLogger<CreateContactTool>.Instance);

        var result = await tool.CreateContact("Alice", accountId: "dav-1", addressBookName: "  Family ");

        Assert.AreEqual("contact-2", JsonDocument.Parse(result).RootElement.GetProperty("contactId").GetString());
        regExp.Verify();
        factExp.Verify();
        davExp.Verify();
    }
}
