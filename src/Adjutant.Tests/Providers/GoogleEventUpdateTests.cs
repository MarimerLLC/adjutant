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
