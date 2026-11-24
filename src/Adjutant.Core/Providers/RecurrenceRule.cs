using System.Globalization;
using System.Xml.Linq;
using Adjutant.Core.Services;
using Adjutant.Core.Utilities;
using Ical.Net;
using Microsoft.Kiota.Abstractions;
using GraphDayOfWeek = Microsoft.Graph.Models.DayOfWeekObject;
using GraphPatternType = Microsoft.Graph.Models.RecurrencePatternType;
using GraphRangeType = Microsoft.Graph.Models.RecurrenceRangeType;
using GraphWeekIndex = Microsoft.Graph.Models.WeekIndex;
using IcalPattern = Ical.Net.DataTypes.RecurrencePattern;

namespace Adjutant.Core.Providers;

/// <summary>
/// A requested recurrence change: a new rule, or <see cref="Clear"/> to make the event a single
/// instance. "No change" is a null <see cref="RecurrenceChange"/>.
/// </summary>
public sealed class RecurrenceChange
{
    public static readonly RecurrenceChange Clear = new(null, null);

    private RecurrenceChange(IcalPattern? pattern, string? rule)
    {
        Pattern = pattern;
        Rule = rule;
    }

    internal static RecurrenceChange For(IcalPattern pattern, string rule) => new(pattern, rule);

    /// <summary>The parsed rule; null when clearing.</summary>
    public IcalPattern? Pattern { get; }

    /// <summary>The RRULE value without the <c>RRULE:</c> prefix, upper-cased; null when clearing.</summary>
    public string? Rule { get; }

    public bool IsClear => Pattern is null;
}

/// <summary>The shapes Graph and EWS can express (both use the same pattern families).</summary>
internal enum RecurrenceKind { Daily, Weekly, AbsoluteMonthly, RelativeMonthly, AbsoluteYearly, RelativeYearly }

/// <summary>
/// A provider-neutral recurrence, resolved against the event's start: defaults RFC 5545 takes
/// from DTSTART (the weekday for WEEKLY, the day for MONTHLY, ...) are filled in.
/// </summary>
/// <param name="Index">Week of the month for relative patterns: 1-4, or -1 for the last.</param>
internal sealed record RecurrenceSpec(
    RecurrenceKind Kind,
    int Interval,
    IReadOnlyList<DayOfWeek> Days,
    int DayOfMonth,
    int Month,
    int Index,
    DayOfWeek FirstDayOfWeek,
    DateOnly StartDate,
    DateOnly? EndDate,
    int? Count);

/// <summary>
/// Parses the RFC 5545 RRULE an agent passes to create_event/update_event and maps it to each
/// provider's format. Google and CalDAV take the rule as written; Graph and EWS take structured
/// patterns, so rules they cannot express are refused rather than approximated.
/// </summary>
public static partial class RecurrenceRule
{
    public const string ClearValue = "none";

    /// <summary>
    /// Null or blank: no change. <c>"none"</c>: <see cref="RecurrenceChange.Clear"/>. Otherwise an
    /// RRULE value, with or without the <c>RRULE:</c> prefix.
    /// </summary>
    /// <exception cref="ProviderOperationException">The rule is malformed or unsupported.</exception>
    public static RecurrenceChange? Parse(string? input)
    {
        if (string.IsNullOrWhiteSpace(input))
            return null;

        var text = input.Trim();
        if (text.Equals(ClearValue, StringComparison.OrdinalIgnoreCase))
            return RecurrenceChange.Clear;

        if (text.StartsWith("RRULE:", StringComparison.OrdinalIgnoreCase))
            text = text[6..].Trim();

        if (text.Contains(':') || text.Contains('\n') || text.Contains('\r'))
            throw Invalid(input, "pass a single RRULE value such as FREQ=WEEKLY;BYDAY=MO, not other iCalendar properties");

        text = text.ToUpperInvariant();
        IcalPattern pattern;
        try
        {
            pattern = new IcalPattern(text);
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException or OverflowException)
        {
            throw Invalid(input, ex.Message);
        }

        if (pattern.Frequency == FrequencyType.None)
            throw Invalid(input, "FREQ is required (DAILY, WEEKLY, MONTHLY or YEARLY)");
        if (pattern.Frequency is FrequencyType.Secondly or FrequencyType.Minutely or FrequencyType.Hourly)
            throw Invalid(input, "events cannot repeat more often than daily");
        // Ical.Net itself refuses COUNT with UNTIL, and COUNT or INTERVAL below 1.
        return RecurrenceChange.For(pattern, text);
    }

    /// <summary>
    /// The rule text for providers that take it as written (Google, CalDAV). RFC 5545 requires
    /// UNTIL to be a UTC date-time when the event is timed, so a date-only UNTIL becomes the end
    /// of that day in <paramref name="timeZone"/> (UTC when unknown), in UTC.
    /// </summary>
    public static string RuleFor(RecurrenceChange change, bool timedEvent, string? timeZone)
    {
        var rule = change.Rule ?? throw new InvalidOperationException("A cleared recurrence has no rule.");
        if (!timedEvent)
            return rule;

        return DateOnlyUntil().Replace(rule, match =>
        {
            var date = DateTime.ParseExact(match.Groups[1].Value, "yyyyMMdd", CultureInfo.InvariantCulture);
            var zone = TimeZoneHelper.TryGetTimeZone(timeZone) ?? TimeZoneInfo.Utc;
            var utc = TimeZoneInfo.ConvertTimeToUtc(date.AddDays(1).AddSeconds(-1), zone);
            return "UNTIL=" + utc.ToString("yyyyMMdd'T'HHmmss'Z'", CultureInfo.InvariantCulture);
        });
    }

    [System.Text.RegularExpressions.GeneratedRegex(@"UNTIL=(\d{8})(?=;|$)")]
    private static partial System.Text.RegularExpressions.Regex DateOnlyUntil();

    private static bool HasCount(IcalPattern p) => p.Count != int.MinValue;
    private static bool HasUntil(IcalPattern p) => p.Until != DateTime.MinValue;
    private static bool HasOffset(Ical.Net.DataTypes.WeekDay d) => d.Offset != int.MinValue;

    private static ProviderOperationException Invalid(string input, string reason) =>
        new($"Invalid recurrence '{input.Trim()}': {reason}.");

    private static ProviderOperationException Unsupported(string provider, string reason) =>
        new($"This recurrence can't be set on {provider} calendars: {reason}. " +
            "Use a simpler rule (daily, weekly on given days, monthly on a day or an nth weekday, or yearly).");

    /// <summary>
    /// Resolves <paramref name="pattern"/> into the pattern families Graph and EWS share.
    /// <paramref name="start"/> is the event's start as local wall time in <paramref name="timeZone"/>.
    /// </summary>
    internal static RecurrenceSpec ToSpec(IcalPattern pattern, DateTime start, string? timeZone, string provider)
    {
        if (pattern.BySecond.Count > 0 || pattern.ByMinute.Count > 0 || pattern.ByHour.Count > 0)
            throw Unsupported(provider, "BYHOUR, BYMINUTE and BYSECOND are not supported");
        if (pattern.ByYearDay.Count > 0 || pattern.ByWeekNo.Count > 0)
            throw Unsupported(provider, "BYYEARDAY and BYWEEKNO are not supported");

        var days = pattern.ByDay.Select(d => d.DayOfWeek).Distinct().ToList();
        var startDate = DateOnly.FromDateTime(start);
        var interval = pattern.Interval;
        var kind = RecurrenceKind.Daily;
        var dayOfMonth = 0;
        var month = 0;
        var index = 0;

        switch (pattern.Frequency)
        {
            case FrequencyType.Daily:
                RequireNone(provider, pattern.ByMonthDay.Count > 0 || pattern.ByMonth.Count > 0 || pattern.BySetPosition.Count > 0,
                    "a DAILY rule can't use BYMONTHDAY, BYMONTH or BYSETPOS");
                if (days.Count > 0)
                {
                    // FREQ=DAILY;BYDAY=MO,TU,WE,TH,FR is the usual way to say "every weekday".
                    RequireNone(provider, interval != 1, "a DAILY rule with BYDAY must have INTERVAL=1; use FREQ=WEEKLY");
                    RequireNone(provider, pattern.ByDay.Any(HasOffset), "a DAILY rule can't use numbered days such as 2MO");
                    kind = RecurrenceKind.Weekly;
                }
                break;

            case FrequencyType.Weekly:
                RequireNone(provider, pattern.ByMonthDay.Count > 0 || pattern.ByMonth.Count > 0 || pattern.BySetPosition.Count > 0,
                    "a WEEKLY rule can't use BYMONTHDAY, BYMONTH or BYSETPOS");
                RequireNone(provider, pattern.ByDay.Any(HasOffset), "a WEEKLY rule can't use numbered days such as 2MO");
                kind = RecurrenceKind.Weekly;
                if (days.Count == 0)
                    days.Add(start.DayOfWeek);
                break;

            case FrequencyType.Monthly:
                RequireNone(provider, pattern.ByMonth.Count > 0, "a MONTHLY rule can't use BYMONTH");
                if (days.Count > 0)
                {
                    RequireNone(provider, pattern.ByMonthDay.Count > 0, "BYDAY and BYMONTHDAY can't be combined");
                    kind = RecurrenceKind.RelativeMonthly;
                    index = WeekIndexOf(pattern, provider);
                }
                else
                {
                    kind = RecurrenceKind.AbsoluteMonthly;
                    dayOfMonth = SingleMonthDay(pattern, start, provider);
                }
                break;

            case FrequencyType.Yearly:
                RequireNone(provider, pattern.ByMonth.Count > 1, "only one BYMONTH value is supported");
                // Without BYMONTH, BYDAY and BYMONTHDAY apply across the whole year (BYDAY=-1MO is
                // the year's last Monday), which these patterns can't express.
                RequireNone(provider, pattern.ByMonth.Count == 0 && (days.Count > 0 || pattern.ByMonthDay.Count > 0),
                    "a YEARLY rule with BYDAY or BYMONTHDAY also needs BYMONTH");
                month = pattern.ByMonth.Count == 1 ? pattern.ByMonth[0] : start.Month;
                if (days.Count > 0)
                {
                    RequireNone(provider, pattern.ByMonthDay.Count > 0, "BYDAY and BYMONTHDAY can't be combined");
                    kind = RecurrenceKind.RelativeYearly;
                    index = WeekIndexOf(pattern, provider);
                }
                else
                {
                    kind = RecurrenceKind.AbsoluteYearly;
                    dayOfMonth = SingleMonthDay(pattern, start, provider);
                }
                break;
        }

        // Graph and Exchange move a missing day (the 31st, Feb 29) to the month's last day, where
        // RFC 5545 skips that month, so they would add occurrences.
        RequireNone(provider, kind == RecurrenceKind.AbsoluteMonthly && dayOfMonth > 28,
            "a monthly rule on the 29th-31st would move to the last day in shorter months instead of skipping them");
        RequireNone(provider, kind == RecurrenceKind.AbsoluteYearly && month == 2 && dayOfMonth == 29,
            "a yearly rule on February 29 would move to February 28 instead of skipping non-leap years");

        DateOnly? endDate = null;
        if (HasUntil(pattern))
        {
            var until = pattern.Until;
            if (until.Kind == DateTimeKind.Utc && TimeZoneHelper.TryGetTimeZone(timeZone) is { } zone)
                until = TimeZoneInfo.ConvertTimeFromUtc(until, zone);
            endDate = DateOnly.FromDateTime(until);
            // The patterns' end date is inclusive, so a cutoff earlier in the day than the meeting
            // (UNTIL=...T090000Z for a 10:00 meeting) excludes that day. A date-only UNTIL (no time,
            // not UTC) includes it.
            var hasTime = pattern.Until.Kind == DateTimeKind.Utc || pattern.Until.TimeOfDay != TimeSpan.Zero;
            if (hasTime && until.TimeOfDay < start.TimeOfDay)
                endDate = endDate.Value.AddDays(-1);
        }

        return new RecurrenceSpec(kind, interval, days, dayOfMonth, month, index, pattern.FirstDayOfWeek,
            startDate, endDate, HasCount(pattern) ? pattern.Count : null);
    }

    private static void RequireNone(string provider, bool violated, string reason)
    {
        if (violated)
            throw Unsupported(provider, reason);
    }

    private static int SingleMonthDay(IcalPattern pattern, DateTime start, string provider)
    {
        RequireNone(provider, pattern.BySetPosition.Count > 0, "BYSETPOS needs BYDAY");
        if (pattern.ByMonthDay.Count == 0)
            return start.Day;
        RequireNone(provider, pattern.ByMonthDay.Count > 1, "only one BYMONTHDAY value is supported");
        var day = pattern.ByMonthDay[0];
        RequireNone(provider, day is < 1 or > 31, "BYMONTHDAY must be between 1 and 31");
        return day;
    }

    /// <summary>The nth-week index from numbered days (2TU, -1FR) or BYSETPOS with plain days.</summary>
    private static int WeekIndexOf(IcalPattern pattern, string provider)
    {
        int index;
        if (pattern.BySetPosition.Count > 0)
        {
            RequireNone(provider, pattern.BySetPosition.Count > 1, "only one BYSETPOS value is supported");
            RequireNone(provider, pattern.ByDay.Any(HasOffset), "BYSETPOS can't be combined with numbered days such as 2MO");
            index = pattern.BySetPosition[0];
        }
        else
        {
            var offsets = pattern.ByDay.Select(d => d.Offset).Distinct().ToList();
            RequireNone(provider, offsets.Contains(int.MinValue),
                "a monthly or yearly BYDAY needs a week number, such as 2TU (second Tuesday) or -1FR (last Friday)");
            // 1MO,1WE means the first Monday and the first Wednesday; these patterns would read it as
            // the first day that is either. "The first of several days" is BYSETPOS.
            RequireNone(provider, pattern.ByDay.Count > 1,
                "a numbered BYDAY such as 2TU takes one day; for the first or last of several days use BYSETPOS " +
                "(BYDAY=MO,TU,WE,TH,FR;BYSETPOS=-1)");
            index = offsets[0];
        }

        RequireNone(provider, index is not (1 or 2 or 3 or 4 or -1), "the week number must be 1-4 or -1 (last)");
        return index;
    }

    // ---- Microsoft Graph ----

    internal static Microsoft.Graph.Models.PatternedRecurrence ToGraph(RecurrenceSpec spec)
    {
        var pattern = new Microsoft.Graph.Models.RecurrencePattern
        {
            Type = spec.Kind switch
            {
                RecurrenceKind.Daily => GraphPatternType.Daily,
                RecurrenceKind.Weekly => GraphPatternType.Weekly,
                RecurrenceKind.AbsoluteMonthly => GraphPatternType.AbsoluteMonthly,
                RecurrenceKind.RelativeMonthly => GraphPatternType.RelativeMonthly,
                RecurrenceKind.AbsoluteYearly => GraphPatternType.AbsoluteYearly,
                _ => GraphPatternType.RelativeYearly
            },
            Interval = spec.Interval
        };

        if (spec.Kind is RecurrenceKind.Weekly or RecurrenceKind.RelativeMonthly or RecurrenceKind.RelativeYearly)
            pattern.DaysOfWeek = spec.Days.Select(d => (GraphDayOfWeek?)ToGraphDay(d)).ToList();
        if (spec.Kind == RecurrenceKind.Weekly)
            pattern.FirstDayOfWeek = ToGraphDay(spec.FirstDayOfWeek);
        if (spec.Kind is RecurrenceKind.AbsoluteMonthly or RecurrenceKind.AbsoluteYearly)
            pattern.DayOfMonth = spec.DayOfMonth;
        if (spec.Kind is RecurrenceKind.AbsoluteYearly or RecurrenceKind.RelativeYearly)
            pattern.Month = spec.Month;
        if (spec.Kind is RecurrenceKind.RelativeMonthly or RecurrenceKind.RelativeYearly)
            pattern.Index = spec.Index switch
            {
                1 => GraphWeekIndex.First,
                2 => GraphWeekIndex.Second,
                3 => GraphWeekIndex.Third,
                4 => GraphWeekIndex.Fourth,
                _ => GraphWeekIndex.Last
            };

        var range = new Microsoft.Graph.Models.RecurrenceRange { StartDate = ToKiotaDate(spec.StartDate) };
        if (spec.Count is { } count)
        {
            range.Type = GraphRangeType.Numbered;
            range.NumberOfOccurrences = count;
        }
        else if (spec.EndDate is { } end)
        {
            range.Type = GraphRangeType.EndDate;
            range.EndDate = ToKiotaDate(end);
        }
        else
        {
            range.Type = GraphRangeType.NoEnd;
        }

        return new Microsoft.Graph.Models.PatternedRecurrence { Pattern = pattern, Range = range };
    }

    private static Date ToKiotaDate(DateOnly date) => new(date.Year, date.Month, date.Day);

    private static GraphDayOfWeek ToGraphDay(DayOfWeek day) => day switch
    {
        DayOfWeek.Sunday => GraphDayOfWeek.Sunday,
        DayOfWeek.Monday => GraphDayOfWeek.Monday,
        DayOfWeek.Tuesday => GraphDayOfWeek.Tuesday,
        DayOfWeek.Wednesday => GraphDayOfWeek.Wednesday,
        DayOfWeek.Thursday => GraphDayOfWeek.Thursday,
        DayOfWeek.Friday => GraphDayOfWeek.Friday,
        _ => GraphDayOfWeek.Saturday
    };

    // ---- Exchange Web Services ----

    /// <summary>
    /// The <c>t:Recurrence</c> element, children in EWS schema order: one pattern element, then
    /// one range element.
    /// </summary>
    internal static XElement ToEws(RecurrenceSpec spec, XNamespace t)
    {
        const string provider = "Exchange";
        XElement Interval() => new(t + "Interval", spec.Interval.ToString(CultureInfo.InvariantCulture));
        XElement Month() => new(t + "Month", CultureInfo.InvariantCulture.DateTimeFormat.GetMonthName(spec.Month));
        XElement DayOfMonth() => new(t + "DayOfMonth", spec.DayOfMonth.ToString(CultureInfo.InvariantCulture));
        XElement Index() => new(t + "DayOfWeekIndex", spec.Index switch
        {
            1 => "First",
            2 => "Second",
            3 => "Third",
            4 => "Fourth",
            _ => "Last"
        });

        if (spec.Kind is RecurrenceKind.AbsoluteYearly or RecurrenceKind.RelativeYearly)
            RequireNone(provider, spec.Interval != 1, "a yearly rule must have INTERVAL=1");

        var pattern = spec.Kind switch
        {
            RecurrenceKind.Daily => new XElement(t + "DailyRecurrence", Interval()),
            RecurrenceKind.Weekly => new XElement(t + "WeeklyRecurrence", Interval(),
                new XElement(t + "DaysOfWeek", string.Join(' ', spec.Days.Select(d => d.ToString()))),
                // Exchange defaults to Sunday; RFC 5545 (WKST) to Monday. It matters for INTERVAL > 1.
                new XElement(t + "FirstDayOfWeek", spec.FirstDayOfWeek.ToString())),
            RecurrenceKind.AbsoluteMonthly => new XElement(t + "AbsoluteMonthlyRecurrence", Interval(), DayOfMonth()),
            RecurrenceKind.RelativeMonthly => new XElement(t + "RelativeMonthlyRecurrence", Interval(),
                new XElement(t + "DaysOfWeek", EwsRelativeDay(spec.Days)), Index()),
            RecurrenceKind.AbsoluteYearly => new XElement(t + "AbsoluteYearlyRecurrence", DayOfMonth(), Month()),
            _ => new XElement(t + "RelativeYearlyRecurrence",
                new XElement(t + "DaysOfWeek", EwsRelativeDay(spec.Days)), Index(), Month())
        };

        var startDate = new XElement(t + "StartDate", FormatDate(spec.StartDate));
        var range = spec.Count is { } count
            ? new XElement(t + "NumberedRecurrence", startDate,
                new XElement(t + "NumberOfOccurrences", count.ToString(CultureInfo.InvariantCulture)))
            : spec.EndDate is { } end
                ? new XElement(t + "EndDateRecurrence", startDate, new XElement(t + "EndDate", FormatDate(end)))
                : new XElement(t + "NoEndRecurrence", startDate);

        return new XElement(t + "Recurrence", pattern, range);
    }

    private static string FormatDate(DateOnly date) => date.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    /// <summary>EWS relative patterns take one day value; a few day sets have their own names.</summary>
    private static string EwsRelativeDay(IReadOnlyList<DayOfWeek> days)
    {
        if (days.Count == 1)
            return days[0].ToString();

        var set = days.ToHashSet();
        var weekdays = new[] { DayOfWeek.Monday, DayOfWeek.Tuesday, DayOfWeek.Wednesday, DayOfWeek.Thursday, DayOfWeek.Friday };
        if (set.SetEquals(weekdays))
            return "Weekday";
        if (set.SetEquals([DayOfWeek.Saturday, DayOfWeek.Sunday]))
            return "WeekendDay";
        if (set.Count == 7)
            return "Day";

        throw Unsupported("Exchange",
            "a monthly or yearly rule can use one day, all weekdays (MO-FR), the weekend (SA,SU) or every day");
    }
}
