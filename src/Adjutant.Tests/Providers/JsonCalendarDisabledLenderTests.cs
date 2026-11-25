using System.Net;
using System.Text.Json;
using Adjutant.Core.Models;
using Adjutant.Core.Providers;
using Adjutant.Core.Services;
using Adjutant.Core.Tools;
using Adjutant.Tests.Helpers;
using Microsoft.Extensions.Logging.Abstractions;
using Rocks;

namespace Adjutant.Tests.Providers;

/// <summary>
/// A JSON calendar on OneDrive that borrows another account's credentials (authAccountId) must
/// stop reading once that account is disabled: disabling an account switches off its token too,
/// not only the tools that act as it.
/// </summary>
[TestClass]
public class JsonCalendarDisabledLenderTests
{
    private const string TimeZone = "America/Chicago";

    private static AccountInfo Lender(bool enabled) =>
        TestData.CreateAccount(id: "work", provider: "microsoft365", enabled: enabled);

    private static readonly AccountInfo Borrower = TestData.CreateAccount(
        id: "team-cal", provider: "json", domains: [], providerConfig: new()
        {
            ["source"] = "onedrive",
            ["oneDrivePath"] = "/Apps/adjutant/calendar.json",
            ["contactsOneDrivePath"] = "/Apps/adjutant/contacts.json",
            ["emailsOneDrivePath"] = "/Apps/adjutant/emails.json",
            ["authAccountId"] = "work"
        });

    private const string DisabledMessage =
        "Account 'team-cal' gets its OneDrive credentials from 'work', which is disabled. " +
        "Re-enable 'work' or point authAccountId at another account.";

    /// <summary>A strict auth mock with no setups: any token request fails the test.</summary>
    private static IM365AuthenticationServiceCreateExpectations NoTokenRequests() => new();

    private static JsonCalendarProviderService Provider(
        IAccountRegistry registry, IM365AuthenticationService auth, IHttpClientFactory? http = null) =>
        new(NullLogger<JsonCalendarProviderService>.Instance, registry, auth, http ?? new StubHttpClientFactory("[]"));

    [TestMethod]
    public async Task Reads_DisabledLender_AreRefusedWithoutRequestingAToken()
    {
        var registry = new MutableAccountRegistry(Lender(enabled: false), Borrower);
        var authExp = NoTokenRequests();
        var provider = Provider(registry, authExp.Instance());

        var reads = new Func<Task>[]
        {
            () => provider.GetCalendarEventsAsync("team-cal"),
            () => provider.GetCalendarEventDetailsAsync("team-cal", "json-calendar", "evt-1"),
            () => provider.GetContactsAsync("team-cal"),
            () => provider.GetEmailsAsync("team-cal"),
        };

        foreach (var read in reads)
        {
            var ex = await Assert.ThrowsExactlyAsync<ProviderOperationException>(read);
            Assert.AreEqual(DisabledMessage, ex.Message);
        }

        authExp.Verify();
    }

    [TestMethod]
    public async Task Read_LenderDisabledAfterAFetch_DoesNotServeTheCachedData()
    {
        var registry = new MutableAccountRegistry(Lender(enabled: true), Borrower);
        var authExp = new IM365AuthenticationServiceCreateExpectations();
        authExp.Setups.GetTokenSilentlyAsync(
                "test-tenant", "test-client", Arg.Any<string[]>(), "work", Arg.Any<CancellationToken>())
            .ReturnValue(Task.FromResult<string?>("token"))
            .ExpectedCallCount(1);
        var provider = Provider(registry, authExp.Instance());

        // Enabled: the file is fetched with the lender's token and cached.
        Assert.AreEqual(0, (await provider.GetCalendarEventsAsync("team-cal")).Count());

        // Disabled: refused before the cache, so neither fresh nor stale data is returned.
        registry.Replace(Lender(enabled: false));
        var ex = await Assert.ThrowsExactlyAsync<ProviderOperationException>(
            () => provider.GetCalendarEventsAsync("team-cal"));
        Assert.AreEqual(DisabledMessage, ex.Message);

        authExp.Verify();
    }

    [TestMethod]
    public async Task FanOut_OnlyEnabledAccountBorrowsFromDisabledLender_ReportsAWarningForThatAccount()
    {
        var registry = new MutableAccountRegistry(Lender(enabled: false), Borrower);
        var authExp = NoTokenRequests();
        var json = Provider(registry, authExp.Instance());

        var factExp = new IProviderServiceFactoryCreateExpectations();
        factExp.Setups.GetProvider("json").ReturnValue(json);
        var tool = new GetCalendarEventsTool(registry, factExp.Instance(), NullLogger<GetCalendarEventsTool>.Instance);

        using var doc = JsonDocument.Parse(await tool.GetCalendarEvents(TimeZone));
        var root = doc.RootElement;

        Assert.AreEqual(0, root.GetProperty("events").GetArrayLength());
        var warnings = root.GetProperty("warnings");
        Assert.AreEqual(1, warnings.GetArrayLength());
        Assert.AreEqual("team-cal", warnings[0].GetProperty("accountId").GetString());
        StringAssert.Contains(warnings[0].GetProperty("error").GetString(), DisabledMessage);

        authExp.Verify();
        factExp.Verify();
    }

    /// <summary>An account registry whose accounts a test can change between calls.</summary>
    private sealed class MutableAccountRegistry(params AccountInfo[] accounts) : IAccountRegistry
    {
        private readonly Dictionary<string, AccountInfo> _accounts = accounts.ToDictionary(a => a.Id);

        public void Replace(AccountInfo account) => _accounts[account.Id] = account;

        public Task<IEnumerable<AccountInfo>> GetAllAccountsAsync() =>
            Task.FromResult<IEnumerable<AccountInfo>>(_accounts.Values.ToList());

        public Task<AccountInfo?> GetAccountAsync(string accountId) =>
            Task.FromResult(_accounts.GetValueOrDefault(accountId));

        public IEnumerable<AccountInfo> GetEnabledAccounts() => _accounts.Values.Where(a => a.Enabled).ToList();

        public IEnumerable<AccountInfo> GetAccountsByProvider(string provider) =>
            _accounts.Values.Where(a => a.Provider == provider).ToList();

        public IEnumerable<AccountInfo> GetAccountsByDomain(string domain) =>
            _accounts.Values.Where(a => a.Domains.Contains(domain)).ToList();
    }

    private sealed class StubHttpClientFactory(string body) : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => new(new StubHandler(body));

        private sealed class StubHandler(string body) : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request, CancellationToken cancellationToken) =>
                Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent(body) });
        }
    }
}
