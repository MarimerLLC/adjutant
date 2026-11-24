using System.ComponentModel;
using System.Text.Json;
using Adjutant.Core.Services;
using Microsoft.Extensions.Logging;
using ModelContextProtocol;
using ModelContextProtocol.Server;

namespace Adjutant.Core.Tools;

/// <summary>
/// MCP tool for creating calendar events
/// </summary>
[McpServerToolType]
public sealed class CreateEventTool(
    IAccountRegistry accountRegistry,
    IProviderServiceFactory providerFactory,
    ILogger<CreateEventTool> logger)
{
    [McpServerTool, Description("Create a calendar event, optionally recurring. Always pass the timeZone parameter using the user's local IANA timezone (e.g. `America/Chicago`, `America/New_York`, `Europe/London`) so events are created at the correct local time. Requires explicit account selection or smart routing. The result's `notifications` says whether attendees were sent invitations.")]
    public async Task<string> CreateEvent(
        [Description("Event subject/title")] string subject,
        [Description("Event start date and time (ISO 8601 format). For an all-day event, a date (yyyy-MM-dd). For a recurring event, the first occurrence.")] DateTime start,
        [Description("Event end date and time (ISO 8601 format). For an all-day event, the exclusive end date (yyyy-MM-dd): a one-day event on 2026-10-01 ends 2026-10-02.")] DateTime end,
        [Description("Account ID to create the event in. Omitting uses the first configured account — provide explicitly to target the correct account. Obtain from list_accounts.")] string? accountId = null,
        [Description("Calendar ID to create the event in. Omit (or pass 'primary') for the default calendar; otherwise obtain from list_calendars.")] string? calendarId = null,
        [Description("Event location")] string? location = null,
        [Description("List of attendee email addresses")] List<string>? attendees = null,
        [Description("Event description/body")] string? body = null,
        [Description("IANA timezone name for the event (e.g. `America/Chicago`, `America/New_York`, `Europe/London`). Required to create events at the correct local time.")] string? timeZone = null,
        [Description("True for an all-day event. start/end are then dates (yyyy-MM-dd, end exclusive) and any time of day is ignored.")] bool isAllDay = false,
        [Description("Makes the event recurring: an RFC 5545 RRULE such as `FREQ=WEEKLY;BYDAY=MO,WE;COUNT=10`, `FREQ=MONTHLY;BYDAY=2TU;UNTIL=20271231T235959Z` or `FREQ=DAILY;INTERVAL=2`. Omit for a single event.")] string? recurrence = null,
        [Description("Send invitations to the attendees (default true). Set false to add the event without emailing them. Microsoft 365/Outlook.com always send invitations.")] bool notifyAttendees = true)
    {
        body = ToolGuard.StripCdataWrapper(body);
        ToolGuard.RequireValidRecurrence(recurrence);

        logger.LogInformation("Creating event: subject={Subject}, start={Start}, end={End}, isAllDay={IsAllDay}, recurring={Recurring}, accountId={AccountId}",
            subject, start, end, isAllDay, !string.IsNullOrWhiteSpace(recurrence), accountId);

        if (isAllDay)
            (start, end) = AllDayRange.Normalize(start, end);

        // Determine which account to use
        Models.AccountInfo account;
        if (!string.IsNullOrEmpty(accountId))
        {
            account = await ToolGuard.RequireAccountAsync(
                accountRegistry, accountId, Models.AccountPermission.CalendarWrite);
        }
        else
        {
            // Fall back to the first enabled account that actually permits the write, so a
            // scoped-out account at the head of the list doesn't hijack the operation.
            var accounts = accountRegistry.GetEnabledAccounts();
            var candidates = ToolGuard.FilterByPermission(
                accounts, Models.AccountPermission.CalendarWrite, logger, "create_event");
            var first = candidates.FirstOrDefault();
            if (first == null)
                throw new McpException("No enabled account permits create event");
            account = first;
        }

        try
        {
            // Create event
            var provider = providerFactory.GetProvider(account.Provider);
            var created = await provider.CreateEventAsync(
                account.Id, calendarId, subject, start, end, location, attendees, body, timeZone, isAllDay,
                recurrence, notifyAttendees, CancellationToken.None);

            var result = new
            {
                success = true,
                eventId = created.EventId,
                accountUsed = account.Id,
                calendarUsed = calendarId ?? "default",
                notifications = created.Notifications
            };

            logger.LogInformation("Created event in account {AccountId}", account.Id);

            return JsonSerializer.Serialize(result, new JsonSerializerOptions
            {
                WriteIndented = true
            });
        }
        catch (Exception ex) when (ex is not McpException)
        {
            logger.LogError(ex, "Error in create_event tool");
            throw ToolGuard.Failure("create event", ex);
        }
    }
}
