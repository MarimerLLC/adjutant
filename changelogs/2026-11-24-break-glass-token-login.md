# Break-Glass Token Login Lasts Until the First Successful Provider Sign-In

**Date**: November 24, 2026
**Issue**: [#125](https://github.com/MarimerLLC/adjutant/issues/125)

---

## Summary

With `AdminAuth:AllowTokenLogin` unset, the admin token disappeared from the console login page
as soon as an OIDC provider was *configured*. A provider saved with a mistake (the client secret's
ID instead of its value, or an app registration that rejects the account) then locked the
operator out of the console they needed to fix it. The only way back was to edit
`appsettings.json` on the host and restart.

Provider edits made in the console also did not reach the OIDC handler until a restart: the first
sign-in after adding a provider failed with `IDX20803 ... https://unconfigured.invalid`, and a
replaced client secret stayed in use. Together the two made a trap. The operator saved a correct
fix, it had no effect, and token login was already gone.

Both are fixed. Unset `AllowTokenLogin` now resolves to `true` until a provider sign-in has
succeeded, and provider edits apply on the next sign-in without a restart.

## What changed

### Token login resolution

- `AdminAuthConfiguration.IsTokenLoginAllowed(bool oidcSignInRecorded)` resolves
  `AllowTokenLogin ?? !oidcSignInRecorded`. Explicit `true` and `false` still override.
- `IAdminUserStore.HasRecordedSignIn` supplies the argument: true once `admin-users.json` holds a
  user. Records are written only after the allow-list check or the claim code has passed, so this
  means a provider is known to work end to end.
- Every caller passes it: the login page, the Settings page, the session policy (a token session
  now ends after the first provider sign-in rather than when a provider is saved), the startup log
  and MCP OAuth availability in Development.
- The login page's "Sign-in failed at the identity provider" message names
  `AdminAuth:AllowTokenLogin` when the token field is not available, so an operator whose provider
  breaks later knows the way back in.

### Provider edits apply without a restart

`AddAdminOidcProviders` registered a change token source for each admin OIDC scheme on the
configuration reload token. The `AdminAuthConfiguration` monitor listens to that same token, and a
monitor rebuilds its options inside the callback. Whenever the OIDC monitor's callback ran first,
`ConfigureAdminOidcOptions` read the `AdminAuth` values from before the edit, so the handler stayed
one edit behind. The order of the two callbacks is not fixed from one reload to the next; in the
file-watcher test the first edit after startup landed and the second did not.

The new `AdminAuthOidcChangeTokenSource` fires from the `AdminAuthConfiguration` monitor's
`OnChange`, which runs only after that monitor has recomputed its value.

## Tests

- `AdminOidcReloadTests` (new): provider edits reach the OIDC handler's options, both through
  explicit reloads and through the console's own path (`IAdminAuthConfigurationService` writes the
  file, the file watcher reloads it), over four consecutive edits. The file-watcher test failed on
  the second edit before the fix.
- `AdminTokenLoginIntegrationTests` (new): against the real login page, the token field stays
  while a configured provider has never been used and goes after a recorded sign-in. The
  break-glass hint appears only when the token field is gone.
- `AdminAuthConfigurationTests` (new): the resolution rule, including explicit overrides.
- `AdminUserStoreTests`: `HasRecordedSignIn`, including after a restart.
- `AdminSessionPolicyTests`, `McpOAuthRulesTests`, `AdminAuthConfigurationServiceTests`: updated
  for the new rule.

## Docs

- `docs/configuration.md`: the `AllowTokenLogin` row, the claim flow, the bootstrapping steps,
  and the two host-side ways back in (`AllowTokenLogin: true`, or deleting `admin-users.json`).
- `docs/security.md`: the break-glass bullet.
