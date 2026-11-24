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

/// <summary>
/// A disabled account is switched off for every tool: naming it explicitly is refused, smart
/// routing never picks it, and a bulk item on it fails on its own. Uses the real
/// <see cref="AccountRegistry"/>, which still returns disabled accounts from GetAccountAsync
/// (the admin console needs them), so the tools themselves must refuse.
/// </summary>
[TestClass]
public class DisabledAccountTests
{
    // Enabled account on microsoft365, disabled one on google: a strict factory set up only for
    // microsoft365 fails the test if the disabled account's provider is ever resolved.
    private static readonly AccountInfo Enabled =
        TestData.CreateAccount(id: "acc-on", provider: "microsoft365", domains: ["on.example"]);

    private static readonly AccountInfo Disabled =
        TestData.CreateAccount(id: "acc-off", provider: "google", enabled: false, domains: ["off.example"]);

    // Disabled first, so "first account that permits it" routing would pick it if it weren't skipped.
    private static AccountRegistry Registry() =>
        new(new TestOptionsMonitor<AdjutantConfiguration>(new AdjutantConfiguration { Accounts = [Disabled, Enabled] }),
            NullLogger<AccountRegistry>.Instance);

    private static AccountRegistry DisabledOnlyRegistry() =>
        new(new TestOptionsMonitor<AdjutantConfiguration>(new AdjutantConfiguration { Accounts = [Disabled] }),
            NullLogger<AccountRegistry>.Instance);

    private static void AssertDisabledError(McpException ex)
    {
        StringAssert.Contains(ex.Message, "Account 'acc-off' is disabled");
    }

    [TestMethod]
    public async Task GetEmailDetails_DisabledAccount_IsRefused()
    {
        using var registry = Registry();
        var factExp = new IProviderServiceFactoryCreateExpectations();
        var tool = new GetEmailDetailsTool(registry, factExp.Instance(), NullLogger<GetEmailDetailsTool>.Instance);

        AssertDisabledError(await Assert.ThrowsExactlyAsync<McpException>(
            () => tool.GetEmailDetails("acc-off", "e1")));
        factExp.Verify();
    }

    [TestMethod]
    public async Task GetEmails_ExplicitDisabledAccount_IsRefused()
    {
        using var registry = Registry();
        var factExp = new IProviderServiceFactoryCreateExpectations();
        var tool = new GetEmailsTool(registry, factExp.Instance(), NullLogger<GetEmailsTool>.Instance);

        AssertDisabledError(await Assert.ThrowsExactlyAsync<McpException>(() => tool.GetEmails("acc-off")));
        factExp.Verify();
    }

    [TestMethod]
    public async Task GetCalendarEvents_ExplicitDisabledAccount_IsRefused()
    {
        using var registry = Registry();
        var factExp = new IProviderServiceFactoryCreateExpectations();
        var tool = new GetCalendarEventsTool(registry, factExp.Instance(), NullLogger<GetCalendarEventsTool>.Instance);

        AssertDisabledError(await Assert.ThrowsExactlyAsync<McpException>(
            () => tool.GetCalendarEvents("America/Chicago", accountId: "acc-off")));
        factExp.Verify();
    }

    [TestMethod]
    public async Task SendEmail_ExplicitDisabledAccount_IsRefused()
    {
        using var registry = Registry();
        var factExp = new IProviderServiceFactoryCreateExpectations();
        var tool = new SendEmailTool(registry, factExp.Instance(), new TestAttachmentStore(),
            NullLogger<SendEmailTool>.Instance);

        AssertDisabledError(await Assert.ThrowsExactlyAsync<McpException>(
            () => tool.SendEmail(["someone@off.example"], "Subject", "Body", accountId: "acc-off")));
        factExp.Verify();
    }

    [TestMethod]
    public async Task SendEmail_SmartRouting_SkipsDisabledDomainMatch()
    {
        // The recipient's domain belongs to the disabled account only, so routing must fall
        // through to the enabled default rather than send from the disabled one.
        using var registry = Registry();

        var provExp = new IProviderServiceCreateExpectations();
        provExp.Setups.SendEmailAsync(
            "acc-on", "someone@off.example", "Subject", "Body", Arg.Any<string>(),
            Arg.Any<List<string>?>(), Arg.Any<IReadOnlyList<OutboundEmailAttachment>?>(),
            Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .ReturnValue(Task.FromResult("msg-1"));

        var factExp = new IProviderServiceFactoryCreateExpectations();
        factExp.Setups.GetProvider("microsoft365").ReturnValue(provExp.Instance());

        var tool = new SendEmailTool(registry, factExp.Instance(), new TestAttachmentStore(),
            NullLogger<SendEmailTool>.Instance);

        using var doc = JsonDocument.Parse(await tool.SendEmail(["someone@off.example"], "Subject", "Body"));

        Assert.IsTrue(doc.RootElement.GetProperty("success").GetBoolean());
        factExp.Verify();
        provExp.Verify();
    }

    [TestMethod]
    public async Task SendEmail_SmartRouting_OnlyDisabledAccounts_IsRefused()
    {
        using var registry = DisabledOnlyRegistry();
        var factExp = new IProviderServiceFactoryCreateExpectations();
        var tool = new SendEmailTool(registry, factExp.Instance(), new TestAttachmentStore(),
            NullLogger<SendEmailTool>.Instance);

        var ex = await Assert.ThrowsExactlyAsync<McpException>(
            () => tool.SendEmail(["someone@off.example"], "Subject", "Body"));

        StringAssert.Contains(ex.Message, "No enabled account permits sending email");
        factExp.Verify();
    }

    [TestMethod]
    public async Task CreateEvent_SmartRouting_SkipsDisabledAccount()
    {
        using var registry = Registry();

        var provExp = new IProviderServiceCreateExpectations();
        provExp.Setups.CreateEventAsync(
            "acc-on", Arg.Any<string?>(), "Standup", Arg.Any<DateTime>(), Arg.Any<DateTime>(),
            Arg.Any<string?>(), Arg.Any<List<string>?>(), Arg.Any<string?>(), Arg.Any<string?>(),
            Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .ReturnValue(Task.FromResult("evt-1"));

        var factExp = new IProviderServiceFactoryCreateExpectations();
        factExp.Setups.GetProvider("microsoft365").ReturnValue(provExp.Instance());

        var tool = new CreateEventTool(registry, factExp.Instance(), NullLogger<CreateEventTool>.Instance);

        using var doc = JsonDocument.Parse(await tool.CreateEvent(
            "Standup", new DateTime(2026, 12, 1, 15, 0, 0), new DateTime(2026, 12, 1, 15, 30, 0),
            timeZone: "America/Chicago"));

        Assert.AreEqual("acc-on", doc.RootElement.GetProperty("accountUsed").GetString());
        factExp.Verify();
        provExp.Verify();
    }

    [TestMethod]
    public async Task BulkDeleteEmails_ItemOnDisabledAccount_FailsOnItsOwn()
    {
        using var registry = Registry();

        var provExp = new IProviderServiceCreateExpectations();
        provExp.Setups.DeleteEmailAsync("acc-on", "e1", Arg.Any<CancellationToken>())
            .ReturnValue(Task.CompletedTask);

        var factExp = new IProviderServiceFactoryCreateExpectations();
        factExp.Setups.GetProvider("microsoft365").ReturnValue(provExp.Instance());

        var tool = new BulkDeleteEmailsTool(registry, factExp.Instance(), NullLogger<BulkDeleteEmailsTool>.Instance);

        var result = await tool.BulkDeleteEmails(
        [
            new BulkEmailItem { AccountId = "acc-on", EmailId = "e1" },
            new BulkEmailItem { AccountId = "acc-off", EmailId = "e2" }
        ]);

        using var doc = JsonDocument.Parse(result);
        Assert.AreEqual(1, doc.RootElement.GetProperty("succeeded").GetInt32());
        Assert.AreEqual(1, doc.RootElement.GetProperty("failed").GetInt32());
        var failedItem = doc.RootElement.GetProperty("results").EnumerateArray()
            .Single(r => r.GetProperty("error").ValueKind == JsonValueKind.String);
        StringAssert.Contains(failedItem.GetProperty("error").GetString(), "Account 'acc-off' is disabled");
        factExp.Verify();
        provExp.Verify();
    }
}
