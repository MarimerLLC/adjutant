# Edit Event Descriptions and Recurrence, and Control Attendee Notifications

**Date**: November 24, 2026
**Issue**: [#121](https://github.com/MarimerLLC/adjutant/issues/121)

---

## Summary

`update_event` could change an event's title, time, location and attendees, but not its
description or recurrence, so an agent couldn't fully edit an existing event. It can now do both,
and `create_event` can make recurring events. Both tools let the caller choose whether attendees are
emailed, and say in the result what happened.

This also fixes a silent gap on Google: events were created and updated without `sendUpdates`, so
Google's default (`none`) applied and **attendees were never emailed**.

## What changed

### `update_event`

- New `body` parameter: replaces the description (HTML allowed); `""` clears it. A CDATA wrapper is
  stripped, as `create_event` already did.
- New `recurrence` parameter: an RFC 5545 RRULE (`FREQ=WEEKLY;BYDAY=MO,WE;COUNT=10`) sets or
  replaces the recurrence; `none` makes the event a single event again. A recurrence change always
  applies to the whole series. Given an occurrence id, the update goes to the series master and the
  result's `eventId` is the series id.
- New `notifyAttendees` parameter (default `true`).
- `location=""` now clears the location on Microsoft 365, Outlook.com and Google. These providers
  used to ignore it; Exchange and CalDAV already cleared it.
- Replacing `attendees` keeps the response of people already invited on Google and CalDAV. These
  used to rebuild every attendee, which reset their replies.

### `create_event`

- New `recurrence` and `notifyAttendees` parameters, as above.

### Results

Both tools return `notifications`: `sent`, `not sent`,
`sent by the provider to any attendees (it cannot be turned off)` (Microsoft 365 / Outlook.com,
whose API always notifies), or `depends on the CalDAV server ...`.

A malformed or unsupported RRULE is refused before any provider call, with a message naming the
problem.

### Providers

- **Recurrence parsing** (`RecurrenceRule`): Ical.Net parses and validates the RRULE. Google and
  CalDAV take it as written. Microsoft 365, Outlook.com and Exchange take structured patterns (daily,
  weekly, absolute/relative monthly, absolute/relative yearly; numbered, end-date or no-end range).
  The rule is mapped to those, and a rule they can't express is refused rather than approximated.
- **Microsoft 365 / Outlook.com**: the two providers now share `GraphEventWriter` for create and
  update. Removing a recurrence sends `"recurrence": null`. Kiota drops null properties, so the null
  goes out as additional data.
- **Google**: `sendUpdates` is `all` or `none` from `notifyAttendees`. Replacing a rule keeps
  EXDATE/RDATE lines. An instance id is resolved to its series for recurrence changes.
- **Exchange (EWS)**: body and recurrence on update; recurrence on create in schema order;
  `StartTimeZone`/`EndTimeZone` are set from `timeZone`, so a series keeps its local time across DST.
  `notifyAttendees=false` sends `SendToNone`. An occurrence id is resolved with
  `RecurringMasterItemId`.
- **CalDAV**: `DESCRIPTION` and `RRULE` on update, and `RRULE` on create. Clearing the recurrence
  also drops RDATE/EXDATE and overridden occurrences.
- `IProviderService.CreateEventAsync`/`UpdateEventAsync` now return `EventWriteResult`
  (`EventId`, `Notifications`) and take `recurrence` and `notifyAttendees` (update also takes `body`).

### Docs

`Skills/calendar.md` has an RRULE cheat sheet and the notification rules. `Skills/providers.md`
gives each provider's behaviour, and `docs/mcp-tools.md` documents the new parameters and
`notifications`.
