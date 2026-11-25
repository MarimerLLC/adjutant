# JSON Calendars Stop Borrowing a Disabled Account's Credentials

**Date:** 2026-11-25
**Version:** 2.2.1
**Issue:** [#133](https://github.com/MarimerLLC/adjutant/issues/133)

---

## Summary

A JSON calendar account whose file lives on OneDrive can reuse another account's Microsoft
credentials through `providerConfig.authAccountId`. When that account was **disabled**, the JSON
calendar kept requesting and using its token, so disabling an account didn't fully switch it off.
#118 made "disabled" mean "switched off for every tool", but that check lives in the tool layer,
and this is the one place a provider acts with a *second* account's credentials.

Now a disabled (or missing) referenced account means the JSON calendar can't authenticate. Its
reads fail before any token is requested:

> Account 'team-cal' gets its OneDrive credentials from 'work', which is disabled. Re-enable
> 'work' or point authAccountId at another account.

## What changed

### `CredentialDelegation` (new, `Adjutant.Core.Services`)

One home for the rules about one account using another's credentials:

- `GetLenderId(account)`: the account a JSON/OneDrive account borrows from, or null.
- `FindBorrowers(accounts, lenderId)`: the accounts that borrow from `lenderId`.
- `CheckLender(borrower, lenderId, lender)`: why the borrowing isn't allowed (lender missing or
  disabled), or null.
- `ResolveLenderAsync(registry, borrower)`: looks up the lender and throws
  `ProviderOperationException` with the `CheckLender` reason.

Multi-user support (#124) will need a "same owner" rule for `authAccountId`. It goes in
`CheckLender` and then applies to every caller.

`AccountValidation.GetAuthDelegateAccountId` and the Files.Read check in `DeviceCodeAuthManager`
now use it. The Files.Read check now counts only JSON accounts whose source is `onedrive`. A local
JSON file with a leftover `authAccountId` no longer adds Files.Read to the referenced account's
sign-in.

### `JsonCalendarProviderService`

- The calendar, contacts and emails loaders check the lender **before the cache**. Data fetched
  with the lender's token before it was disabled isn't served afterwards, either as a fresh cache
  hit or as the stale fallback used after a failed fetch.
- `FetchFromOneDriveAsync` resolves the lender through `CredentialDelegation`, so a disabled
  lender's token is never requested.
- A fan-out (`get_calendar_events`, `get_contacts`, `get_emails`, ...) reports the refusal as that
  JSON account's `warnings` entry. The rest of the call goes ahead.

### Admin console

- **Edit Account:** unticking **Enabled** on an account that JSON calendars borrow from lists those
  accounts and warns that they can't read their files while it's disabled.
- **Delete Account:** lists the same accounts, which stop working once it's deleted.
- **Add / Edit Account (JSON on OneDrive):** the Auth Account picker marks disabled accounts
  `(disabled)` and warns when the selected one is disabled.

## Tests

- `CredentialDelegationTests`: lender lookup (both `json` aliases, case-insensitive keys, local
  source, blank or missing `authAccountId`, non-JSON providers), `FindBorrowers`, and
  `CheckLender` for enabled, disabled and missing lenders.
- `JsonCalendarDisabledLenderTests`:
  - Every read (events, event details, contacts, emails) with a disabled lender fails with the
    message above and requests no token.
  - After a successful fetch, disabling the lender makes the next read fail rather than return
    cached data. Exactly one token is requested.
  - A `get_calendar_events` fan-out whose only enabled account is such a JSON calendar returns
    no events and one `warnings` entry for that account, not an error for the whole call.

All three provider tests fail without the fix.

## Docs

`docs/mcp-tools.md` notes that a disabled account's credentials are switched off for JSON
calendars that borrow them.
