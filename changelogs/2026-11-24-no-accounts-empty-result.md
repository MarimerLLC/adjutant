# Read Tools Return an Empty Result on a Server With No Accounts

**Date**: November 24, 2026
**Issue**: [#118](https://github.com/MarimerLLC/adjutant/issues/118)

---

## Summary

On a server with no accounts configured, `list_accounts` returned `{"accounts": []}`, but every
read tool that fans out over all accounts threw an MCP error (`No accounts found`, or
`No accounts configured` from `get_contextual_email_summary`). An agent that had already called
`list_accounts` knew there were no accounts and still got an error, and the message didn't say
how to fix it. The startup log's hint ("Add accounts using the CLI") never reached the client.

"No accounts" is the limiting case of the fan-outs skipping unusable accounts, so it is no longer
an error. The tools return their normal, empty result with one entry in the existing `warnings`
array that says what to do.

## What changed

### `ToolGuard`

- `NoAccountsConfiguredMessage`: "No enabled accounts are configured on this server. Add one in
  the admin console (HTTP server) or with the CLI (e.g. 'adjutant-cli add-m365-account'), or
  enable an existing one." It names both routes because the stdio server registers the same tools
  and has no admin console.
- `NoAccountsConfiguredWarning()`: the `warnings` entry, `{ "warning": "<message>" }`. It has no
  `accountId`, since it isn't about any one account.
- `GetFanOutAccounts(registry, warnings)`: the accounts every all-accounts fan-out starts from,
  the **enabled** ones, adding the no-accounts warning when there are none.
- `NoPermittedAccountsWarning(permission)`: the `No accounts permit ...` text as a `warnings`
  entry, for the calendar fan-outs.

### Tools

When `accountId` is omitted and there are no enabled accounts to fan out to, these tools return an
empty result plus the no-accounts warning instead of throwing:

- `get_emails`, `search_emails` (`emails: []`)
- `list_calendars` (`calendars: []`)
- `get_calendar_events` (`events: []`), including when only a `calendarId` is given, which used
  to fail with "No calendar found ... Provide accountId"
- `get_contacts`, `search_contacts` (`contacts: []`)
- `get_contextual_email_summary`: the same payload it returns when no emails match
  (`message`, `searchKeywords`, `accountsSearched: 0`, `warnings`)

```json
{
  "emails": [],
  "warnings": [
    { "warning": "No enabled accounts are configured on this server. Add one in the admin console (HTTP server) or with the CLI (e.g. 'adjutant-cli add-m365-account'), or enable an existing one." }
  ]
}
```

Related fixes in the same fan-outs:

- **Disabled accounts are no longer queried.** `get_emails`, `search_emails`, `get_contacts`,
  `search_contacts` and `get_contextual_email_summary` started from every configured account,
  disabled ones included, while the calendar fan-outs used only enabled accounts. All seven now
  use enabled accounts.
- **Calendar fan-outs say when every account is scoped out.** When enabled accounts exist but
  none permit reading calendars (e.g. an IMAP-only server, or calendar-read revoked),
  `list_calendars` and `get_calendar_events` used to return an empty list with no warning. They
  now add one `warnings` entry, `No accounts permit reading calendars. ...`. This stays a warning
  rather than an error, matching how `get_calendar_events` treats an explicitly named email-only
  account.
- **`get_contextual_email_summary` output is camelCase,** as `docs/mcp-tools.md` documents.
  It serialized its models with no naming policy, so it emitted `TotalEmails`, `Warnings[].AccountId`,
  `Warnings[].Error` and so on. **Clients reading the PascalCase names must switch.**

### Still errors

- An explicit `accountId` that doesn't exist (`Account '<id>' not found`).
- For the email and contacts fan-outs, enabled accounts that exist but none permit the operation
  (`No accounts permit ...`). That is a configuration refusal the caller should see as one.
- Input validation, e.g. an empty `query` for `search_emails` / `search_contacts`.

## Tests

The tests that asserted the old errors now assert the empty result and the single account-less
warning, through a shared `NoAccountsAssert` helper. Fan-out tests stub `GetEnabledAccounts()`.

New tests:

- `list_calendars`, `get_contacts` and `search_contacts` with no accounts.
- `get_contacts` still throws `No accounts permit ...` when accounts exist but none permit contacts.
- `NoEnabledAccountsTests`: every fan-out against a real `AccountRegistry` holding only a disabled
  account, including `get_calendar_events` with only a `calendarId`. Also a test that a disabled
  account beside an enabled one is never queried.
- `list_calendars` / `get_calendar_events` with only an IMAP account return the permission warning.
- `get_calendar_events` with only a `calendarId` on an empty server.

## Docs

`docs/mcp-tools.md`, `Skills/accounts.md` and `Skills/overview.md` describe the no-accounts warning,
the calendar permission warning, and that fan-outs only use enabled accounts.
