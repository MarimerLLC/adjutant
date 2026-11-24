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
public class UpdateEventToolTests
{
    private static readonly DateTime Start = new(2025, 6, 1, 10, 0, 0);
    private static readonly DateTime End = new(2025, 6, 1, 11, 0, 0);

    [TestMethod]
    public async Task UpdateEvent_Success()
    {
        var account = TestData.CreateAccount(id: "acc-1", provider: "microsoft365");

        var regExp = new IAccountRegistryCreateExpectations();
        regExp.Setups.GetAccountAsync("acc-1")
            .ReturnValue(Task.FromResult<AccountInfo?>(account));

        var provExp = new IProviderServiceCreateExpectations();
        provExp.Setups.UpdateEventAsync(
            "acc-1", "cal-1", "ev-1",
            Arg.Any<string?>(), Arg.Any<DateTime?>(), Arg.Any<DateTime?>(),
            Arg.Any<string?>(), Arg.Any<List<string>?>(), Arg.Any<string?>(), Arg.Any<bool?>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .ReturnValue(Task.FromResult(new EventWriteResult("ev-1", AttendeeNotifications.Sent)));

        var factExp = new IProviderServiceFactoryCreateExpectations();
        factExp.Setups.GetProvider("microsoft365").ReturnValue(provExp.Instance());

        var tool = new UpdateEventTool(regExp.Instance(), factExp.Instance(),
            NullLogger<UpdateEventTool>.Instance);

        var result = await tool.UpdateEvent("acc-1", "cal-1", "ev-1", subject: "Updated", start: Start, end: End, timeZone: "America/Chicago");
        var doc = JsonDocument.Parse(result);

        Assert.IsTrue(doc.RootElement.GetProperty("success").GetBoolean());
        Assert.AreEqual("ev-1", doc.RootElement.GetProperty("eventId").GetString());

        regExp.Verify();
        factExp.Verify();
        provExp.Verify();
    }

    [TestMethod]
    public async Task UpdateEvent_PassesBodyRecurrenceAndNotifyAndReportsTheResult()
    {
        var account = TestData.CreateAccount(id: "acc-1", provider: "google");

        var regExp = new IAccountRegistryCreateExpectations();
        regExp.Setups.GetAccountAsync("acc-1")
            .ReturnValue(Task.FromResult<AccountInfo?>(account));

        var provExp = new IProviderServiceCreateExpectations();
        provExp.Setups.UpdateEventAsync(
            "acc-1", "cal-1", "instance-1",
            Arg.Any<string?>(), Arg.Any<DateTime?>(), Arg.Any<DateTime?>(),
            Arg.Any<string?>(), Arg.Any<List<string>?>(), "America/Chicago", Arg.Any<bool?>(),
            "<p>Agenda</p>", "FREQ=WEEKLY;BYDAY=MO", false, Arg.Any<CancellationToken>())
            .ReturnValue(Task.FromResult(new EventWriteResult("series-1", AttendeeNotifications.NotSent)));

        var factExp = new IProviderServiceFactoryCreateExpectations();
        factExp.Setups.GetProvider("google").ReturnValue(provExp.Instance());

        var tool = new UpdateEventTool(regExp.Instance(), factExp.Instance(),
            NullLogger<UpdateEventTool>.Instance);

        var result = await tool.UpdateEvent("acc-1", "cal-1", "instance-1", timeZone: "America/Chicago",
            body: "<![CDATA[<p>Agenda</p>]]>", recurrence: "FREQ=WEEKLY;BYDAY=MO", notifyAttendees: false);
        var doc = JsonDocument.Parse(result);

        // A recurrence change made through an occurrence reports the series it changed.
        Assert.AreEqual("series-1", doc.RootElement.GetProperty("eventId").GetString());
        Assert.AreEqual("not sent", doc.RootElement.GetProperty("notifications").GetString());

        regExp.Verify();
        factExp.Verify();
        provExp.Verify();
    }

    [TestMethod]
    public async Task UpdateEvent_BlankTextIsUnchangedAndNoneClears()
    {
        var account = TestData.CreateAccount(id: "acc-1", provider: "google");

        var regExp = new IAccountRegistryCreateExpectations();
        regExp.Setups.GetAccountAsync("acc-1")
            .ReturnValue(Task.FromResult<AccountInfo?>(account));

        // body "" (a client filling in unset params) reaches the provider as null (unchanged);
        // location "none" reaches it as "" (clear).
        var provExp = new IProviderServiceCreateExpectations();
        provExp.Setups.UpdateEventAsync(
            "acc-1", "cal-1", "ev-1",
            Arg.Any<string?>(), Arg.Any<DateTime?>(), Arg.Any<DateTime?>(),
            "", Arg.Any<List<string>?>(), Arg.Any<string?>(), Arg.Any<bool?>(),
            Arg.IsDefault<string?>(), Arg.Any<string?>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .ReturnValue(Task.FromResult(new EventWriteResult("ev-1", AttendeeNotifications.Sent)));

        var factExp = new IProviderServiceFactoryCreateExpectations();
        factExp.Setups.GetProvider("google").ReturnValue(provExp.Instance());

        var tool = new UpdateEventTool(regExp.Instance(), factExp.Instance(),
            NullLogger<UpdateEventTool>.Instance);

        await tool.UpdateEvent("acc-1", "cal-1", "ev-1", location: " None ", body: "");

        regExp.Verify();
        factExp.Verify();
        provExp.Verify();
    }

    [TestMethod]
    public async Task UpdateEvent_InvalidRecurrence_IsRefusedBeforeAnyLookup()
    {
        var regExp = new IAccountRegistryCreateExpectations();
        var factExp = new IProviderServiceFactoryCreateExpectations();
        var tool = new UpdateEventTool(regExp.Instance(), factExp.Instance(),
            NullLogger<UpdateEventTool>.Instance);

        var ex = await Assert.ThrowsExactlyAsync<McpException>(
            () => tool.UpdateEvent("acc-1", "cal-1", "ev-1", recurrence: "FREQ=HOURLY"));

        StringAssert.Contains(ex.Message, "Invalid recurrence");
        regExp.Verify();
        factExp.Verify();
    }

    [TestMethod]
    public async Task UpdateEvent_AccountNotFound_ThrowsMcpException()
    {
        var regExp = new IAccountRegistryCreateExpectations();
        regExp.Setups.GetAccountAsync("nonexistent")
            .ReturnValue(Task.FromResult<AccountInfo?>(null));

        var factExp = new IProviderServiceFactoryCreateExpectations();
        var tool = new UpdateEventTool(regExp.Instance(), factExp.Instance(),
            NullLogger<UpdateEventTool>.Instance);

        var ex = await Assert.ThrowsExactlyAsync<McpException>(
            () => tool.UpdateEvent("nonexistent", "cal-1", "ev-1"));
        Assert.AreEqual("Account 'nonexistent' not found", ex.Message);
        regExp.Verify();
    }

    [TestMethod]
    [DataRow(true)]
    [DataRow(false)]
    public async Task UpdateEvent_IsAllDayWithoutStartAndEnd_ThrowsMcpException(bool isAllDay)
    {
        var regExp = new IAccountRegistryCreateExpectations();
        var factExp = new IProviderServiceFactoryCreateExpectations();
        var tool = new UpdateEventTool(regExp.Instance(), factExp.Instance(),
            NullLogger<UpdateEventTool>.Instance);

        var ex = await Assert.ThrowsExactlyAsync<McpException>(
            () => tool.UpdateEvent("acc-1", "cal-1", "ev-1", start: Start, isAllDay: isAllDay));

        Assert.AreEqual("isAllDay requires both start and end.", ex.Message);
    }

    [TestMethod]
    public async Task UpdateEvent_AllDay_PassesNormalizedDatesToProvider()
    {
        var account = TestData.CreateAccount(id: "acc-1", provider: "microsoft365");

        var regExp = new IAccountRegistryCreateExpectations();
        regExp.Setups.GetAccountAsync("acc-1")
            .ReturnValue(Task.FromResult<AccountInfo?>(account));

        var provExp = new IProviderServiceCreateExpectations();
        provExp.Setups.UpdateEventAsync(
            "acc-1", "cal-1", "ev-1",
            Arg.Any<string?>(), (DateTime?)new DateTime(2025, 6, 1), (DateTime?)new DateTime(2025, 6, 2),
            Arg.Any<string?>(), Arg.Any<List<string>?>(), Arg.Any<string?>(), (bool?)true, Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .ReturnValue(Task.FromResult(new EventWriteResult("ev-1", AttendeeNotifications.Sent)));

        var factExp = new IProviderServiceFactoryCreateExpectations();
        factExp.Setups.GetProvider("microsoft365").ReturnValue(provExp.Instance());

        var tool = new UpdateEventTool(regExp.Instance(), factExp.Instance(),
            NullLogger<UpdateEventTool>.Instance);

        // Start and End share a date (10:00-11:00), so this becomes a one-day event.
        await tool.UpdateEvent("acc-1", "cal-1", "ev-1", start: Start, end: End, isAllDay: true);

        regExp.Verify();
        factExp.Verify();
        provExp.Verify();
    }
}
