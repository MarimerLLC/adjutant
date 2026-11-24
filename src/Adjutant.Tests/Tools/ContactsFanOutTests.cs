using Adjutant.Core.Models;
using Adjutant.Core.Services;
using Adjutant.Core.Tools;
using Adjutant.Tests.Helpers;
using Microsoft.Extensions.Logging.Abstractions;
using ModelContextProtocol;
using Rocks;

namespace Adjutant.Tests.Tools;

/// <summary>
/// How get_contacts and search_contacts fan out over "all accounts" when there is nothing to
/// fan out to.
/// </summary>
[TestClass]
public class ContactsFanOutTests
{
    [TestMethod]
    public async Task GetContacts_NoAccounts_ReturnsEmptyWithWarning()
    {
        var regExp = new IAccountRegistryCreateExpectations();
        regExp.Setups.GetAllAccountsAsync()
            .ReturnValue(Task.FromResult<IEnumerable<AccountInfo>>([]));

        var factExp = new IProviderServiceFactoryCreateExpectations();
        var tool = new GetContactsTool(regExp.Instance(), factExp.Instance(),
            NullLogger<GetContactsTool>.Instance);

        NoAccountsAssert.IsEmptyWithWarning(await tool.GetContacts(), "contacts");
        regExp.Verify();
        factExp.Verify();
    }

    [TestMethod]
    public async Task SearchContacts_NoAccounts_ReturnsEmptyWithWarning()
    {
        var regExp = new IAccountRegistryCreateExpectations();
        regExp.Setups.GetAllAccountsAsync()
            .ReturnValue(Task.FromResult<IEnumerable<AccountInfo>>([]));

        var factExp = new IProviderServiceFactoryCreateExpectations();
        var tool = new SearchContactsTool(regExp.Instance(), factExp.Instance(),
            NullLogger<SearchContactsTool>.Instance);

        NoAccountsAssert.IsEmptyWithWarning(await tool.SearchContacts("smith"), "contacts");
        regExp.Verify();
        factExp.Verify();
    }

    [TestMethod]
    public async Task GetContacts_AccountsExistButNonePermitContacts_StillThrows()
    {
        // Accounts that exist but are scoped out are a configuration refusal, not "no accounts".
        var emailOnly = TestData.CreateAccount(
            id: "acc-email",
            permissions: AccountPermissions.None.With(AccountPermission.EmailRead, true));

        var regExp = new IAccountRegistryCreateExpectations();
        regExp.Setups.GetAllAccountsAsync()
            .ReturnValue(Task.FromResult<IEnumerable<AccountInfo>>([emailOnly]));

        var tool = new GetContactsTool(regExp.Instance(),
            new IProviderServiceFactoryCreateExpectations().Instance(),
            NullLogger<GetContactsTool>.Instance);

        var ex = await Assert.ThrowsExactlyAsync<McpException>(() => tool.GetContacts());

        StringAssert.Contains(ex.Message, "No accounts permit");
        regExp.Verify();
    }
}
