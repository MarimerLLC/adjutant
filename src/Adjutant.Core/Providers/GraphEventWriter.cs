using System.Globalization;
using Adjutant.Core.Models;
using Adjutant.Core.Utilities;
using Microsoft.Graph;
using Microsoft.Graph.Models;
using Microsoft.Graph.Models.ODataErrors;

namespace Adjutant.Core.Providers;

/// <summary>
/// Event create/update for the Microsoft Graph providers (Microsoft 365 and Outlook.com), which
/// share the same calendar API.
/// </summary>
/// <remarks>
/// Graph always sends invitations and updates to attendees when the organizer creates or
/// changes a meeting, and has no switch to suppress them, so <c>notifyAttendees</c> is not
/// honoured here; the result says so instead.
/// </remarks>
internal static class GraphEventWriter
{
    private const string ProviderName = "Microsoft 365 / Outlook.com";

    public static async Task<EventWriteResult> CreateAsync(
        GraphServiceClient graphClient,
        string? calendarId,
        string subject,
        DateTime start,
        DateTime end,
        string? location,
        List<string>? attendees,
        string? body,
        string? timeZone,
        bool isAllDay,
        string? recurrence,
        CancellationToken cancellationToken)
    {
        var change = RecurrenceRule.Parse(recurrence);

        var newEvent = new Event
        {
            Subject = subject,
            IsAllDay = isAllDay,
            Start = EventTimeBuilder.ToGraph(start, timeZone, isAllDay),
            End = EventTimeBuilder.ToGraph(end, timeZone, isAllDay)
        };

        if (!string.IsNullOrEmpty(location))
            newEvent.Location = new Location { DisplayName = location };

        if (!string.IsNullOrEmpty(body))
            newEvent.Body = new ItemBody { ContentType = BodyType.Html, Content = body };

        if (attendees != null && attendees.Count > 0)
            newEvent.Attendees = ToAttendees(attendees);

        if (change is { IsClear: false })
            newEvent.Recurrence = RecurrenceRule.ToGraph(
                RecurrenceRule.ToSpec(change.Pattern!, start, timeZone, ProviderName));

        Event? createdEvent;
        if (CalendarIdAliases.IsDefaultCalendar(calendarId))
        {
            createdEvent = await graphClient.Me.Calendar.Events.PostAsync(newEvent, cancellationToken: cancellationToken);
        }
        else
        {
            try
            {
                createdEvent = await graphClient.Me.Calendars[calendarId].Events.PostAsync(newEvent, cancellationToken: cancellationToken);
            }
            catch (ODataError ex) when (CalendarIdAliases.TranslateCreateFailure(calendarId, ex) is { } unknownCalendar)
            {
                throw unknownCalendar;
            }
        }

        return new EventWriteResult(createdEvent?.Id ?? string.Empty,
            AttendeeNotifications.AnyIn(attendees) ? AttendeeNotifications.SentByProvider : AttendeeNotifications.NoAttendees);
    }

    public static async Task<EventWriteResult> UpdateAsync(
        GraphServiceClient graphClient,
        string eventId,
        string? subject,
        DateTime? start,
        DateTime? end,
        string? location,
        List<string>? attendees,
        string? timeZone,
        bool? isAllDay,
        string? body,
        string? recurrence,
        CancellationToken cancellationToken)
    {
        var change = RecurrenceRule.Parse(recurrence);
        var targetId = eventId;
        DateTime? seriesStart = start;
        // The zone the series' dates are read in: the caller's, else the event's own, so an
        // evening meeting west of UTC isn't anchored on the next (UTC) day.
        var recurrenceZone = timeZone;
        bool? hasAttendees = attendees is null ? null : AttendeeNotifications.AnyIn(attendees);

        if (change is not null)
        {
            // Recurrence lives on the series master; an occurrence or exception id (what
            // get_calendar_events lists for a recurring meeting) can't carry one.
            var existing = await GetForRecurrenceAsync(graphClient, eventId, cancellationToken);
            if (existing?.Type is EventType.Occurrence or EventType.Exception
                && !string.IsNullOrEmpty(existing.SeriesMasterId))
            {
                targetId = existing.SeriesMasterId;
                existing = await GetForRecurrenceAsync(graphClient, targetId, cancellationToken);
            }

            recurrenceZone ??= existing?.OriginalStartTimeZone;
            seriesStart ??= LocalStart(existing, recurrenceZone);
            hasAttendees ??= existing?.Attendees is { } existingAttendees ? existingAttendees.Count > 0 : null;
        }

        var eventUpdate = BuildUpdate(subject, start, end, location, attendees, timeZone, isAllDay, body, change, seriesStart,
            recurrenceZone);

        await graphClient.Me.Events[targetId].PatchAsync(eventUpdate, cancellationToken: cancellationToken);
        return new EventWriteResult(targetId,
            hasAttendees == false ? AttendeeNotifications.NoAttendees : AttendeeNotifications.SentByProvider);
    }

    /// <summary>
    /// The PATCH body. Only fields set here are sent; clearing the recurrence sends
    /// <c>"recurrence": null</c>, which makes the series a single event.
    /// </summary>
    internal static Event BuildUpdate(
        string? subject,
        DateTime? start,
        DateTime? end,
        string? location,
        List<string>? attendees,
        string? timeZone,
        bool? isAllDay,
        string? body,
        RecurrenceChange? change,
        DateTime? seriesStart,
        string? recurrenceZone = null)
    {
        var eventUpdate = new Event();

        if (!string.IsNullOrEmpty(subject))
            eventUpdate.Subject = subject;

        if (isAllDay.HasValue)
            eventUpdate.IsAllDay = isAllDay.Value;

        if (start.HasValue)
            eventUpdate.Start = EventTimeBuilder.ToGraph(start.Value, timeZone, isAllDay == true);

        if (end.HasValue)
            eventUpdate.End = EventTimeBuilder.ToGraph(end.Value, timeZone, isAllDay == true);

        if (location is not null)
            eventUpdate.Location = new Location { DisplayName = location };

        if (body is not null)
            eventUpdate.Body = new ItemBody { ContentType = BodyType.Html, Content = body };

        if (attendees != null)
            eventUpdate.Attendees = ToAttendees(attendees);

        if (change is not null)
        {
            if (change.IsClear)
            {
                // Kiota skips null properties, even ones explicitly set, so the null that
                // removes the recurrence has to go out as additional data.
                eventUpdate.AdditionalData["recurrence"] = null!;
            }
            else
            {
                if (seriesStart is null)
                    throw new Services.ProviderOperationException(
                        "Couldn't read the event's start to anchor the recurrence; pass start and end with it.");
                eventUpdate.Recurrence = RecurrenceRule.ToGraph(
                    RecurrenceRule.ToSpec(change.Pattern!, seriesStart.Value, recurrenceZone ?? timeZone, ProviderName));
            }
        }

        return eventUpdate;
    }

    private static Task<Event?> GetForRecurrenceAsync(GraphServiceClient graphClient, string id, CancellationToken ct) =>
        graphClient.Me.Events[id].GetAsync(config =>
            config.QueryParameters.Select = ["type", "seriesMasterId", "start", "isAllDay", "originalStartTimeZone", "attendees"], ct);

    /// <summary>
    /// The event's start as local wall time in <paramref name="timeZone"/>. Without a Prefer
    /// header Graph returns UTC; all-day dates are kept as written.
    /// </summary>
    internal static DateTime? LocalStart(Event? evt, string? timeZone)
    {
        if (evt?.Start?.DateTime is not { } raw
            || !DateTime.TryParse(raw, CultureInfo.InvariantCulture, DateTimeStyles.None, out var value))
            return null;

        if (evt.IsAllDay == true)
            return value.Date;

        var source = TimeZoneHelper.TryGetTimeZone(evt.Start.TimeZone) ?? TimeZoneInfo.Utc;
        var target = TimeZoneHelper.TryGetTimeZone(timeZone);
        if (target is null || target.Id == source.Id)
            return value;

        var utc = TimeZoneInfo.ConvertTimeToUtc(DateTime.SpecifyKind(value, DateTimeKind.Unspecified), source);
        return TimeZoneInfo.ConvertTimeFromUtc(utc, target);
    }

    private static List<Attendee> ToAttendees(List<string> attendees) => attendees
        .Where(email => !string.IsNullOrWhiteSpace(email))
        .Select(email => new Attendee
        {
            EmailAddress = new Microsoft.Graph.Models.EmailAddress { Address = email.Trim() },
            Type = AttendeeType.Required
        })
        .ToList();
}
