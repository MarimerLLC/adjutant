using Adjutant.Core.Providers;
using Google.Apis.Calendar.v3.Data;

namespace Adjutant.Tests.Providers;

/// <summary>
/// How the Google provider rewrites an existing event's recurrence and attendees on update.
/// </summary>
[TestClass]
public class GoogleEventUpdateTests
{
    [TestMethod]
    public void ApplyRecurrence_ReplacesTheRuleAndKeepsDateLines()
    {
        var existing = new List<string> { "RRULE:FREQ=DAILY;COUNT=5", "EXDATE;TZID=America/Chicago:20261203T090000" };

        var lines = GoogleProviderService.ApplyRecurrence(existing, RecurrenceRule.Parse("FREQ=WEEKLY;BYDAY=MO")!);

        CollectionAssert.AreEqual(new[] { "RRULE:FREQ=WEEKLY;BYDAY=MO", "EXDATE;TZID=America/Chicago:20261203T090000" }, lines);
    }

    [TestMethod]
    public void ApplyRecurrence_DateOnlyUntilOnATimedEventIsSentInUtc()
    {
        var lines = GoogleProviderService.ApplyRecurrence(null, RecurrenceRule.Parse("FREQ=WEEKLY;BYDAY=TU;UNTIL=20271231")!,
            timedEvent: true, timeZone: "America/Chicago");

        CollectionAssert.AreEqual(new[] { "RRULE:FREQ=WEEKLY;BYDAY=TU;UNTIL=20280101T055959Z" }, lines);
    }

    [TestMethod]
    public void FillMissingTimeZones_LabelsTimedBoundariesWithoutMovingThem()
    {
        var evt = new Event
        {
            Start = new EventDateTime { DateTimeRaw = "2026-12-01T09:00:00-06:00" },
            End = new EventDateTime { DateTimeRaw = "2026-12-01T10:00:00-06:00", TimeZone = "America/New_York" }
        };

        GoogleProviderService.FillMissingTimeZones(evt, RecurrenceRule.Parse("FREQ=DAILY")!, "America/Chicago");

        Assert.AreEqual("America/Chicago", evt.Start.TimeZone);
        Assert.AreEqual("2026-12-01T09:00:00-06:00", evt.Start.DateTimeRaw);
        Assert.AreEqual("America/New_York", evt.End.TimeZone, "an existing zone is kept");
    }

    [TestMethod]
    public void FillMissingTimeZones_LeavesAllDayEventsAlone()
    {
        var evt = new Event { Start = new EventDateTime { Date = "2026-12-01" }, End = new EventDateTime { Date = "2026-12-02" } };

        GoogleProviderService.FillMissingTimeZones(evt, RecurrenceRule.Parse("FREQ=YEARLY")!, "America/Chicago");

        Assert.IsNull(evt.Start.TimeZone);
    }

    [TestMethod]
    public void ApplyRecurrence_ClearIsAnEmptyListNotNull()
    {
        var lines = GoogleProviderService.ApplyRecurrence(["RRULE:FREQ=DAILY", "RDATE:20261224T090000Z"], RecurrenceChange.Clear);

        Assert.IsNotNull(lines);
        Assert.AreEqual(0, lines.Count);
    }

    [TestMethod]
    public void MergeAttendees_KeepsExistingRepliesAndDropsDuplicates()
    {
        var existing = new List<EventAttendee>
        {
            new() { Email = "al@example.com", ResponseStatus = "accepted" },
            new() { Email = "gone@example.com", ResponseStatus = "declined" }
        };

        var merged = GoogleProviderService.MergeAttendees(existing, ["AL@example.com", " bo@example.com ", "bo@example.com", ""]);

        Assert.AreEqual(2, merged.Count);
        Assert.AreSame(existing[0], merged[0]);
        Assert.AreEqual("bo@example.com", merged[1].Email);
        Assert.IsNull(merged[1].ResponseStatus);
    }
}
