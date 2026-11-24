using System.Xml.Linq;
using Adjutant.Core.Providers;
using Adjutant.Core.Services;
using Microsoft.Graph.Models;

namespace Adjutant.Tests.Providers;

/// <summary>
/// RRULE parsing and its mapping to the structured patterns Graph and EWS take.
/// </summary>
[TestClass]
public class RecurrenceRuleTests
{
    private static readonly XNamespace T = "http://schemas.microsoft.com/exchange/services/2006/types";

    // A Wednesday, 10:00 local.
    private static readonly DateTime Start = new(2026, 12, 2, 10, 0, 0);

    [TestMethod]
    [DataRow(null)]
    [DataRow("")]
    [DataRow("   ")]
    public void Parse_BlankMeansNoChange(string? input) =>
        Assert.IsNull(RecurrenceRule.Parse(input));

    [TestMethod]
    [DataRow("none")]
    [DataRow(" NONE ")]
    public void Parse_NoneClears(string input) =>
        Assert.IsTrue(RecurrenceRule.Parse(input)!.IsClear);

    [TestMethod]
    public void Parse_AcceptsPrefixAndLowercase()
    {
        var change = RecurrenceRule.Parse("RRULE:freq=weekly;byday=mo,we;count=10")!;

        Assert.IsFalse(change.IsClear);
        Assert.AreEqual("FREQ=WEEKLY;BYDAY=MO,WE;COUNT=10", change.Rule);
        Assert.AreEqual(10, change.Pattern!.Count);
    }

    [TestMethod]
    [DataRow("garbage", "FREQ is required")]
    [DataRow("FREQ=HOURLY", "more often than daily")]
    [DataRow("FREQ=WEEKLY;COUNT=3;UNTIL=20270101T000000Z", "COUNT and UNTIL")]
    [DataRow("FREQ=WEEKLY;BYDAY=XX", "XX")]
    [DataRow("FREQ=WEEKLY;COUNT=abc", "Invalid recurrence")]
    [DataRow("FREQ=WEEKLY;COUNT=0", "COUNT value 0")]
    [DataRow("FREQ=DAILY;INTERVAL=0", "INTERVAL")]
    [DataRow("DTSTART:20261202T100000\nRRULE:FREQ=DAILY", "single RRULE value")]
    public void Parse_RefusesBadRules(string input, string expected)
    {
        var ex = Assert.ThrowsExactly<ProviderOperationException>(() => RecurrenceRule.Parse(input));
        StringAssert.Contains(ex.Message, expected);
    }

    [TestMethod]
    public void Graph_WeeklyOnDaysWithCount()
    {
        var graph = ToGraph("FREQ=WEEKLY;BYDAY=MO,WE;COUNT=10");

        Assert.AreEqual(RecurrencePatternType.Weekly, graph.Pattern!.Type);
        Assert.AreEqual(1, graph.Pattern.Interval);
        CollectionAssert.AreEqual(new DayOfWeekObject?[] { DayOfWeekObject.Monday, DayOfWeekObject.Wednesday }, graph.Pattern.DaysOfWeek!.ToArray());
        Assert.AreEqual(RecurrenceRangeType.Numbered, graph.Range!.Type);
        Assert.AreEqual(10, graph.Range.NumberOfOccurrences);
        Assert.AreEqual("2026-12-02", graph.Range.StartDate.ToString());
    }

    [TestMethod]
    public void Graph_WeeklyWithoutDaysUsesTheStartDay()
    {
        var graph = ToGraph("FREQ=WEEKLY;INTERVAL=2");

        CollectionAssert.AreEqual(new DayOfWeekObject?[] { DayOfWeekObject.Wednesday }, graph.Pattern!.DaysOfWeek!.ToArray());
        Assert.AreEqual(2, graph.Pattern.Interval);
        Assert.AreEqual(RecurrenceRangeType.NoEnd, graph.Range!.Type);
    }

    [TestMethod]
    public void Graph_DailyOnWeekdaysBecomesWeekly()
    {
        var graph = ToGraph("FREQ=DAILY;BYDAY=MO,TU,WE,TH,FR");

        Assert.AreEqual(RecurrencePatternType.Weekly, graph.Pattern!.Type);
        Assert.AreEqual(5, graph.Pattern.DaysOfWeek!.Count);
    }

    [TestMethod]
    public void Graph_PlainDaily()
    {
        var graph = ToGraph("FREQ=DAILY;INTERVAL=3");

        Assert.AreEqual(RecurrencePatternType.Daily, graph.Pattern!.Type);
        Assert.AreEqual(3, graph.Pattern.Interval);
        Assert.IsNull(graph.Pattern.DaysOfWeek);
    }

    [TestMethod]
    public void Graph_MonthlyOnTheSecondTuesday()
    {
        var graph = ToGraph("FREQ=MONTHLY;BYDAY=2TU");

        Assert.AreEqual(RecurrencePatternType.RelativeMonthly, graph.Pattern!.Type);
        Assert.AreEqual(WeekIndex.Second, graph.Pattern.Index);
        CollectionAssert.AreEqual(new DayOfWeekObject?[] { DayOfWeekObject.Tuesday }, graph.Pattern.DaysOfWeek!.ToArray());
    }

    [TestMethod]
    public void Graph_MonthlyOnTheLastWeekdayViaSetPos()
    {
        var graph = ToGraph("FREQ=MONTHLY;BYDAY=MO,TU,WE,TH,FR;BYSETPOS=-1");

        Assert.AreEqual(RecurrencePatternType.RelativeMonthly, graph.Pattern!.Type);
        Assert.AreEqual(WeekIndex.Last, graph.Pattern.Index);
        Assert.AreEqual(5, graph.Pattern.DaysOfWeek!.Count);
    }

    [TestMethod]
    public void Graph_MonthlyWithoutPartsUsesTheStartDay()
    {
        var graph = ToGraph("FREQ=MONTHLY");

        Assert.AreEqual(RecurrencePatternType.AbsoluteMonthly, graph.Pattern!.Type);
        Assert.AreEqual(2, graph.Pattern.DayOfMonth);
    }

    [TestMethod]
    public void Graph_YearlyOnAMonthDay()
    {
        var graph = ToGraph("FREQ=YEARLY;BYMONTH=7;BYMONTHDAY=4");

        Assert.AreEqual(RecurrencePatternType.AbsoluteYearly, graph.Pattern!.Type);
        Assert.AreEqual(7, graph.Pattern.Month);
        Assert.AreEqual(4, graph.Pattern.DayOfMonth);
    }

    [TestMethod]
    public void Graph_YearlyOnTheLastMondayOfMay()
    {
        var graph = ToGraph("FREQ=YEARLY;BYMONTH=5;BYDAY=-1MO");

        Assert.AreEqual(RecurrencePatternType.RelativeYearly, graph.Pattern!.Type);
        Assert.AreEqual(5, graph.Pattern.Month);
        Assert.AreEqual(WeekIndex.Last, graph.Pattern.Index);
    }

    [TestMethod]
    public void Graph_UntilInUtcEndsOnTheLocalDate()
    {
        // 2027-01-01 03:00Z is still Dec 31 in Chicago.
        var graph = ToGraph("FREQ=DAILY;UNTIL=20270101T030000Z", "America/Chicago");

        Assert.AreEqual(RecurrenceRangeType.EndDate, graph.Range!.Type);
        Assert.AreEqual("2026-12-31", graph.Range.EndDate.ToString());
    }

    [TestMethod]
    [DataRow("FREQ=MONTHLY;BYDAY=TU", "week number")]
    [DataRow("FREQ=MONTHLY;BYDAY=5FR", "1-4 or -1")]
    [DataRow("FREQ=MONTHLY;BYMONTHDAY=1,15", "one BYMONTHDAY")]
    [DataRow("FREQ=DAILY;BYHOUR=9", "BYHOUR")]
    [DataRow("FREQ=WEEKLY;BYDAY=2MO", "numbered days")]
    [DataRow("FREQ=YEARLY;BYWEEKNO=20", "BYWEEKNO")]
    [DataRow("FREQ=MONTHLY;BYDAY=1MO,1WE", "BYSETPOS")]
    [DataRow("FREQ=MONTHLY;BYMONTHDAY=31", "shorter months")]
    [DataRow("FREQ=YEARLY;BYMONTH=2;BYMONTHDAY=29", "February 29")]
    [DataRow("FREQ=YEARLY;BYDAY=-1MO", "needs BYMONTH")]
    [DataRow("FREQ=YEARLY;BYMONTHDAY=4", "needs BYMONTH")]
    public void Graph_RefusesWhatItCannotExpress(string rule, string expected)
    {
        var ex = Assert.ThrowsExactly<ProviderOperationException>(() => ToGraph(rule));
        StringAssert.Contains(ex.Message, expected);
    }

    [TestMethod]
    public void Graph_UntilBeforeTheMeetingTimeExcludesThatDay()
    {
        // The meeting is at 10:00 (UTC here); a 09:00Z cutoff on Dec 4 ends the series on Dec 3.
        Assert.AreEqual("2026-12-03", ToGraph("FREQ=DAILY;UNTIL=20261204T090000Z").Range!.EndDate.ToString());
        Assert.AreEqual("2026-12-04", ToGraph("FREQ=DAILY;UNTIL=20261204T100000Z").Range!.EndDate.ToString());
        // A date-only UNTIL includes its day.
        Assert.AreEqual("2026-12-04", ToGraph("FREQ=DAILY;UNTIL=20261204").Range!.EndDate.ToString());
    }

    [TestMethod]
    public void Graph_BareYearlyUsesTheStartDate()
    {
        var graph = ToGraph("FREQ=YEARLY");

        Assert.AreEqual(RecurrencePatternType.AbsoluteYearly, graph.Pattern!.Type);
        Assert.AreEqual(12, graph.Pattern.Month);
        Assert.AreEqual(2, graph.Pattern.DayOfMonth);
    }

    [TestMethod]
    public void RuleFor_TimedEventGetsAUtcEndOfDayUntil()
    {
        var change = RecurrenceRule.Parse("FREQ=WEEKLY;UNTIL=20271231;BYDAY=TU")!;

        // 23:59:59 on Dec 31 in Chicago (UTC-6) is 05:59:59Z on Jan 1.
        Assert.AreEqual("FREQ=WEEKLY;UNTIL=20280101T055959Z;BYDAY=TU", RecurrenceRule.RuleFor(change, true, "America/Chicago"));
        Assert.AreEqual("FREQ=WEEKLY;UNTIL=20271231;BYDAY=TU", RecurrenceRule.RuleFor(change, false, "America/Chicago"));
    }

    [TestMethod]
    public void RuleFor_LeavesADateTimeUntilAlone()
    {
        var change = RecurrenceRule.Parse("FREQ=DAILY;UNTIL=20271231T235959Z")!;
        Assert.AreEqual("FREQ=DAILY;UNTIL=20271231T235959Z", RecurrenceRule.RuleFor(change, true, "America/Chicago"));
    }

    [TestMethod]
    public void Ews_WeeklyFollowsSchemaOrder()
    {
        var recurrence = ToEws("FREQ=WEEKLY;BYDAY=MO,WE;COUNT=10");

        CollectionAssert.AreEqual(new[] { "WeeklyRecurrence", "NumberedRecurrence" },
            recurrence.Elements().Select(e => e.Name.LocalName).ToArray());
        var weekly = recurrence.Element(T + "WeeklyRecurrence")!;
        CollectionAssert.AreEqual(new[] { "Interval", "DaysOfWeek", "FirstDayOfWeek" }, weekly.Elements().Select(e => e.Name.LocalName).ToArray());
        Assert.AreEqual("Monday Wednesday", weekly.Element(T + "DaysOfWeek")!.Value);
        // RFC 5545's default WKST, not Exchange's Sunday.
        Assert.AreEqual("Monday", weekly.Element(T + "FirstDayOfWeek")!.Value);
        var range = recurrence.Element(T + "NumberedRecurrence")!;
        CollectionAssert.AreEqual(new[] { "StartDate", "NumberOfOccurrences" }, range.Elements().Select(e => e.Name.LocalName).ToArray());
        Assert.AreEqual("2026-12-02", range.Element(T + "StartDate")!.Value);
        Assert.AreEqual("10", range.Element(T + "NumberOfOccurrences")!.Value);
    }

    [TestMethod]
    public void Ews_LastWeekdayOfTheMonthUsesTheWeekdayName()
    {
        var relative = ToEws("FREQ=MONTHLY;BYDAY=MO,TU,WE,TH,FR;BYSETPOS=-1").Element(T + "RelativeMonthlyRecurrence")!;

        CollectionAssert.AreEqual(new[] { "Interval", "DaysOfWeek", "DayOfWeekIndex" }, relative.Elements().Select(e => e.Name.LocalName).ToArray());
        Assert.AreEqual("Weekday", relative.Element(T + "DaysOfWeek")!.Value);
        Assert.AreEqual("Last", relative.Element(T + "DayOfWeekIndex")!.Value);
    }

    [TestMethod]
    public void Ews_YearlyAndEndDate()
    {
        var recurrence = ToEws("FREQ=YEARLY;BYMONTH=7;BYMONTHDAY=4;UNTIL=20301231");

        var yearly = recurrence.Element(T + "AbsoluteYearlyRecurrence")!;
        CollectionAssert.AreEqual(new[] { "DayOfMonth", "Month" }, yearly.Elements().Select(e => e.Name.LocalName).ToArray());
        Assert.AreEqual("July", yearly.Element(T + "Month")!.Value);
        Assert.AreEqual("2030-12-31", recurrence.Element(T + "EndDateRecurrence")!.Element(T + "EndDate")!.Value);
    }

    [TestMethod]
    [DataRow("FREQ=YEARLY;INTERVAL=2", "INTERVAL=1")]
    [DataRow("FREQ=MONTHLY;BYDAY=1MO,1WE", "one day")]
    public void Ews_RefusesWhatItCannotExpress(string rule, string expected)
    {
        var ex = Assert.ThrowsExactly<ProviderOperationException>(() => ToEws(rule));
        StringAssert.Contains(ex.Message, "Exchange");
        StringAssert.Contains(ex.Message, expected);
    }

    private static PatternedRecurrence ToGraph(string rule, string? timeZone = null) =>
        RecurrenceRule.ToGraph(RecurrenceRule.ToSpec(RecurrenceRule.Parse(rule)!.Pattern!, Start, timeZone, "Microsoft 365"));

    private static XElement ToEws(string rule) =>
        RecurrenceRule.ToEws(RecurrenceRule.ToSpec(RecurrenceRule.Parse(rule)!.Pattern!, Start, null, "Exchange"), T);
}
