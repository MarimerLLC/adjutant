using System.ComponentModel;
using System.Text.Json;
using Adjutant.Core.Models;
using Adjutant.Core.Services;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace Adjutant.Core.Tools;

/// <summary>
/// MCP tool for updating calendar events
/// </summary>
[McpServerToolType]
public sealed class UpdateEventTool(
    IAccountRegistry accountRegistry,
    IProviderServiceFactory providerFactory,
    ILogger<UpdateEventTool> logger)
{
    [McpServerTool, Description("Update an existing calendar event: title, time, location, description, attendees or recurrence. Pass only what changes. Always pass the timeZone parameter using the user's local IANA timezone (e.g. `America/Chicago`, `America/New_York`, `Europe/London`) when updating start/end times or recurrence so the event stays at the correct local time. The result's `notifications` says whether attendees were sent the update.")]
    public async Task<string> UpdateEvent(
        [Description("Account ID that owns the event. Obtain from list_accounts.")] string accountId,
        [Description("Calendar ID that contains the event. Obtain from list_calendars.")] string calendarId,
        [Description("Event ID to update. Obtain from get_calendar_events or get_calendar_event_details. For a recurring event, an occurrence's id changes only that occurrence, except that a recurrence change always applies to the whole series.")] string eventId,
        [Description("New event subject/title")] string? subject = null,
        [Description("New event start date and time (ISO 8601 format). For an all-day event, a date (yyyy-MM-dd).")] DateTime? start = null,
        [Description("New event end date and time (ISO 8601 format). For an all-day event, the exclusive end date (yyyy-MM-dd).")] DateTime? end = null,
        [Description("New event location. Pass `none` to clear it; an empty string leaves it unchanged.")] string? location = null,
        [Description("New list of attendee email addresses. Replaces the whole list; attendees already invited keep their response.")] List<string>? attendees = null,
        [Description("IANA timezone name for the event times (e.g. `America/Chicago`, `America/New_York`, `Europe/London`). Required when updating start or end times.")] string? timeZone = null,
        [Description("Set true to make the event all-day (start/end become dates, end exclusive) or false to make it timed. Requires both start and end. Pass true when moving an existing all-day event. Omit to leave it unchanged.")] bool? isAllDay = null,
        [Description("New event description/body (HTML allowed). Replaces the existing description. Pass `none` to clear it; an empty string leaves it unchanged.")] string? body = null,
        [Description("Set or replace the recurrence with an RFC 5545 RRULE such as `FREQ=WEEKLY;BYDAY=MO,WE;COUNT=10`, or pass `none` to make the event a single event again (this removes all its other occurrences). Applies to the whole series even when eventId is an occurrence. Omit to leave the recurrence unchanged.")] string? recurrence = null,
        [Description("Send the update to the attendees (default true). Set false to change the event without emailing them. Microsoft 365/Outlook.com always send updates.")] bool notifyAttendees = true)
    {
        logger.LogInformation("Updating event: eventId={EventId}, accountId={AccountId}, calendarId={CalendarId}",
            eventId, accountId, calendarId);

        ToolGuard.RequireNonEmpty(accountId, nameof(accountId));
        ToolGuard.RequireNonEmpty(calendarId, nameof(calendarId));
        ToolGuard.RequireNonEmpty(eventId, nameof(eventId));
        ToolGuard.RequireValidRecurrence(recurrence);
        // Some clients send "" for optional parameters they mean to leave out, so blank means
        // "unchanged" and only "none" clears (as for recurrence). Providers take "" as clear.
        body = ClearableText(ToolGuard.StripCdataWrapper(body));
        location = ClearableText(location);

        if (isAllDay.HasValue)
        {
            if (!start.HasValue || !end.HasValue)
                throw new McpException("isAllDay requires both start and end.");

            if (isAllDay.Value)
            {
                var (allDayStart, allDayEnd) = AllDayRange.Normalize(start.Value, end.Value);
                (start, end) = (allDayStart, allDayEnd);
            }
        }

        var account = await ToolGuard.RequireAccountAsync(
            accountRegistry, accountId, AccountPermission.CalendarWrite);

        try
        {
            var provider = providerFactory.GetProvider(account.Provider);
            var updated = await provider.UpdateEventAsync(
                accountId, calendarId, eventId, subject, start, end, location, attendees, timeZone, isAllDay,
                body, recurrence, notifyAttendees, CancellationToken.None);

            return JsonSerializer.Serialize(new
            {
                success = true,
                eventId = updated.EventId,
                accountUsed = accountId,
                calendarUsed = calendarId,
                notifications = updated.Notifications
            }, new JsonSerializerOptions { WriteIndented = true });
        }
        catch (Exception ex) when (ex is not McpException)
        {
            logger.LogError(ex, "Error in update_event tool");
            throw ToolGuard.Failure("update event", ex);
        }
    }

    /// <summary>Blank: no change (null). <c>none</c>: clear (""). Anything else as given.</summary>
    private static string? ClearableText(string? value) =>
        string.IsNullOrWhiteSpace(value) ? null
        : value.Trim().Equals("none", StringComparison.OrdinalIgnoreCase) ? ""
        : value;
}
