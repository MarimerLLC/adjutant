using System.Text.Json;
using Adjutant.Core.Configuration;
using Adjutant.Core.Models;
using Adjutant.Core.Providers;
using Adjutant.Core.Services;
using Adjutant.Core.Tools;
using Adjutant.Tests.Helpers;
using Microsoft.Extensions.Logging.Abstractions;
using Rocks;

namespace Adjutant.Tests.Tools;

/// <summary>
/// Every all-accounts fan-out treats a server whose accounts are all disabled the same as one
/// with no accounts: an empty result plus the no-accounts warning, and no provider is touched.
/// Uses the real <see cref="AccountRegistry"/>, since the enabled filter lives there.
/// </summary>
[TestClass]
public class NoEnabledAccountsTests
{
    private const string TimeZone = "America/Chicago";

    private static AccountRegistry DisabledOnlyRegistry() =>
        new(new TestOptionsMonitor<AdjutantConfiguration>(new AdjutantConfiguration
        {
            Accounts = [TestData.CreateAccount(id: "acc-off", provider: "microsoft365", enabled: false)]
        }), NullLogger<AccountRegistry>.Instance);

    // No setups: a strict factory fails the test if any provider is resolved.
    private static IProviderServiceFactoryCreateExpectations NoProviders() => new();

    [TestMethod]
    public async Task GetEmails_OnlyDisabledAccounts_ReturnsEmptyWithWarning()
    {
        using var registry = DisabledOnlyRegistry();
        var factExp = NoProviders();
        var tool = new GetEmailsTool(registry, factExp.Instance(), NullLogger<GetEmailsTool>.Instance);

        NoAccountsAssert.IsEmptyWithWarning(await tool.GetEmails(), "emails");
        factExp.Verify();
    }

    [TestMethod]
    public async Task SearchEmails_OnlyDisabledAccounts_ReturnsEmptyWithWarning()
    {
        using var registry = DisabledOnlyRegistry();
        var factExp = NoProviders();
        var tool = new SearchEmailsTool(registry, factExp.Instance(), NullLogger<SearchEmailsTool>.Instance);

        NoAccountsAssert.IsEmptyWithWarning(await tool.SearchEmails("invoice"), "emails");
        factExp.Verify();
    }

    [TestMethod]
    public async Task GetContacts_OnlyDisabledAccounts_ReturnsEmptyWithWarning()
    {
        using var registry = DisabledOnlyRegistry();
        var factExp = NoProviders();
        var tool = new GetContactsTool(registry, factExp.Instance(), NullLogger<GetContactsTool>.Instance);

        NoAccountsAssert.IsEmptyWithWarning(await tool.GetContacts(), "contacts");
        factExp.Verify();
    }

    [TestMethod]
    public async Task SearchContacts_OnlyDisabledAccounts_ReturnsEmptyWithWarning()
    {
        using var registry = DisabledOnlyRegistry();
        var factExp = NoProviders();
        var tool = new SearchContactsTool(registry, factExp.Instance(), NullLogger<SearchContactsTool>.Instance);

        NoAccountsAssert.IsEmptyWithWarning(await tool.SearchContacts("smith"), "contacts");
        factExp.Verify();
    }

    [TestMethod]
    public async Task ListCalendars_OnlyDisabledAccounts_ReturnsEmptyWithWarning()
    {
        using var registry = DisabledOnlyRegistry();
        var factExp = NoProviders();
        var tool = new ListCalendarsTool(registry, factExp.Instance(), NullLogger<ListCalendarsTool>.Instance);

        NoAccountsAssert.IsEmptyWithWarning(await tool.ListCalendars(), "calendars");
        factExp.Verify();
    }

    [TestMethod]
    public async Task GetCalendarEvents_OnlyDisabledAccounts_ReturnsEmptyWithWarning()
    {
        using var registry = DisabledOnlyRegistry();
        var factExp = NoProviders();
        var tool = new GetCalendarEventsTool(registry, factExp.Instance(), NullLogger<GetCalendarEventsTool>.Instance);

        NoAccountsAssert.IsEmptyWithWarning(await tool.GetCalendarEvents(TimeZone), "events");
        factExp.Verify();
    }

    [TestMethod]
    public async Task GetCalendarEvents_CalendarIdOnly_OnlyDisabledAccounts_ReturnsEmptyWithWarning()
    {
        // calendarId without accountId used to fail with "No calendar found ... provide
        // accountId", which sends the agent the wrong way when there's no account at all.
        using var registry = DisabledOnlyRegistry();
        var factExp = NoProviders();
        var tool = new GetCalendarEventsTool(registry, factExp.Instance(), NullLogger<GetCalendarEventsTool>.Instance);

        NoAccountsAssert.IsEmptyWithWarning(
            await tool.GetCalendarEvents(TimeZone, calendarId: "cal-remembered"), "events");
        factExp.Verify();
    }

    [TestMethod]
    public async Task GetContextualEmailSummary_OnlyDisabledAccounts_ReturnsEmptyWithWarning()
    {
        using var registry = DisabledOnlyRegistry();
        var factExp = NoProviders();
        var tool = new GetContextualEmailSummaryTool(registry, factExp.Instance(),
            NullLogger<GetContextualEmailSummaryTool>.Instance);

        var result = await tool.GetContextualEmailSummary();

        NoAccountsAssert.IsEmptyWithWarning(result, resultProperty: null);
        using var doc = JsonDocument.Parse(result);
        Assert.AreEqual(0, doc.RootElement.GetProperty("accountsSearched").GetInt32());
        factExp.Verify();
    }

    [TestMethod]
    public async Task GetEmails_DisabledAccountAlongsideEnabledOne_IsNotQueried()
    {
        // The fan-out used to start from every configured account, disabled ones included.
        var enabled = TestData.CreateAccount(id: "acc-on", provider: "microsoft365");
        var disabled = TestData.CreateAccount(id: "acc-off", provider: "google", enabled: false);
        using var registry = new AccountRegistry(
            new TestOptionsMonitor<AdjutantConfiguration>(new AdjutantConfiguration { Accounts = [enabled, disabled] }),
            NullLogger<AccountRegistry>.Instance);

        var provExp = new IProviderServiceCreateExpectations();
        provExp.Setups.GetEmailsAsync("acc-on", Arg.Any<int>(), Arg.Any<bool>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .ReturnValue(Task.FromResult<IEnumerable<EmailMessage>>([TestData.CreateEmail(id: "e1", accountId: "acc-on")]));

        // Only the enabled account's provider may be resolved; a "google" lookup fails the test.
        var factExp = new IProviderServiceFactoryCreateExpectations();
        factExp.Setups.GetProvider("microsoft365").ReturnValue(provExp.Instance());

        var tool = new GetEmailsTool(registry, factExp.Instance(), NullLogger<GetEmailsTool>.Instance);
        using var doc = JsonDocument.Parse(await tool.GetEmails());

        var emails = doc.RootElement.GetProperty("emails");
        Assert.AreEqual(1, emails.GetArrayLength());
        Assert.AreEqual("acc-on", emails[0].GetProperty("accountId").GetString());
        Assert.AreEqual(JsonValueKind.Null, doc.RootElement.GetProperty("warnings").ValueKind);
        factExp.Verify();
        provExp.Verify();
    }
}
