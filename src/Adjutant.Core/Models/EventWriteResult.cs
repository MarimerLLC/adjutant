namespace Adjutant.Core.Models;

/// <summary>
/// The outcome of creating or updating a calendar event.
/// </summary>
/// <param name="EventId">
/// The event that was written. For an update this can differ from the id passed in: a
/// recurrence change made through an occurrence id is applied to its series.
/// </param>
/// <param name="Notifications">What attendees were told; one of <see cref="AttendeeNotifications"/>.</param>
public sealed record EventWriteResult(string EventId, string Notifications);

/// <summary>
/// What a create or update did about notifying attendees, as reported to the MCP caller.
/// </summary>
public static class AttendeeNotifications
{
    /// <summary>Invitations or updates were sent to the attendees.</summary>
    public const string Sent = "sent";

    /// <summary>The caller asked for no notifications and the provider honoured it.</summary>
    public const string NotSent = "not sent";

    /// <summary>Microsoft Graph always notifies attendees of an organizer's change.</summary>
    public const string SentByProvider = "sent by the provider to any attendees (it cannot be turned off)";

    /// <summary>CalDAV has no client-side send; the server may schedule implicitly.</summary>
    public const string ServerDependent = "depends on the CalDAV server (some send invitations, some don't)";

    public static string For(bool notifyAttendees) => notifyAttendees ? Sent : NotSent;
}
