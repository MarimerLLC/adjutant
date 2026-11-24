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

- `NoAccountsConfiguredMessage`: "No enabled accounts are configured on this server. Add one (or
  enable an existing one) in the admin console, or add one with the CLI (e.g.
  'adjutant-cli add-m365-account')." The wording says "enabled" because the calendar fan-outs
  only consider enabled accounts, so a server whose accounts are all disabled gets the same warning.
- `NoAccountsConfiguredWarning()`: the `warnings` entry, `{ "warning": "<message>" }`. It has no
  `accountId`, since it isn't about any one account.

### Tools

When `accountId` is omitted and there are no accounts to fan out to, these tools return an empty
result plus that warning instead of throwing:

- `get_emails`, `search_emails` (`emails: []`)
- `list_calendars` (`calendars: []`)
- `get_calendar_events` (`events: []`)
- `get_contacts`, `search_contacts` (`contacts: []`)
- `get_contextual_email_summary`: the same payload it returns when no emails match
  (`message`, `searchKeywords`, `accountsSearched: 0`, `warnings`)

```json
{
  "emails": [],
  "warnings": [
    { "warning": "No enabled accounts are configured on this server. Add one (or enable an existing one) in the admin console, or add one with the CLI (e.g. 'adjutant-cli add-m365-account')." }
  ]
}
```

### Still errors

- An explicit `accountId` that doesn't exist (`Account '<id>' not found`).
- Accounts that exist but none permit the operation (`No accounts permit ...`). That is a
  configuration refusal the caller should see as one.
- Input validation, e.g. an empty `query` for `search_emails` / `search_contacts`.

## Tests

The tests that asserted the old errors now assert the empty result and the single account-less
warning, through a shared `NoAccountsAssert` helper. New tests cover `list_calendars`,
`get_contacts` and `search_contacts`, plus a check that `get_contacts` still throws
`No accounts permit ...` when accounts exist but none permit reading contacts.

## Docs

`docs/mcp-tools.md`, `Skills/accounts.md` and `Skills/overview.md` describe the no-accounts warning.
