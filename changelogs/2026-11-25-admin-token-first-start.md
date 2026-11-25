# Admin token generated at first start

**Date:** 2026-11-25
**Issue:** [#131](https://github.com/MarimerLLC/adjutant/issues/131)

With no `ADJUTANT_ADMIN_TOKEN` / `Adjutant:AdminToken`, which was the default for a new
container, the admin surface was open. `AdminAuthMiddleware` passed every `/admin/*` request
through as "development mode", and the console login page accepted any token. The MCP endpoint
already failed safe in the same situation by generating a key.

## Change

- **`IAdminTokenStore` / `AdminTokenStore`** (new, `Adjutant.Core.Security`). A configured token
  wins and is read on every check. Without one, `EnsureToken()` generates `adjadm_<random>` and
  stores only its SHA-256 in `{data}/admin-token.json`. `Matches()` compares SHA-256 digests with
  `FixedTimeEquals`, and is now the one comparison used by both the middleware and `Login.razor`.
  This replaces the duplicated `TokenMatches` / `TokensMatch`. If the file can't be written, the
  hash is kept in memory for that run and a warning is logged.
- **Startup banner.** `AdminAuthStartup.ConfigureAdminAuth` calls `EnsureToken()` before the
  server listens and prints a generated token once, beside the MCP key and claim-code banners.
  On later starts it logs which token is in effect.
- **No open branches.** The "no token means allow everything" code is gone from
  `AdminAuthMiddleware` and `Login.razor`. So is `AdminAuthConfiguration.IsTokenFormOffered`,
  which hid the form outside Development only because it accepted anything. The login page now
  follows `IsTokenLoginAllowed` alone.
- **No opt-out.** No local-development switch was needed: a dev run gets a token in its console
  output like any other.
- **docker-compose.yml** no longer defaults the token to `changeme`. Left unset, the server
  generates one (a blank value counts as unset).
- Docs: configuration.md ("The admin token") and security.md.

## Review follow-ups

- **Generated secrets stay out of log files.** The banner's secret line goes to standard output
  only (`StartupSecrets.Show`), and Serilog records a placeholder in its place. Before, the token
  reached `{data}/logs/adjutant-http-*.log` and any OTLP backend in plain text. The first-start MCP
  API key banner had the same flaw and now works the same way. `docker logs` and `kubectl logs`
  still show both secrets.
- **Token sessions are bound to the token.** A token login stores a fingerprint of the token in
  effect (`IAdminTokenStore.Fingerprint`, a labelled second hash, in claim
  `adjutant:admin_token`). `AdminSessionPolicy.Evaluate` ends a token session whose fingerprint is
  missing or stale (`Reason.TokenChanged`). The check now also runs on every request through the
  cookie's `OnValidatePrincipal`, as well as on circuit revalidation and on the OAuth consent page.
  Replacing the token, by setting `ADJUTANT_ADMIN_TOKEN` or deleting `admin-token.json`, ends the
  sessions opened with the old one. So do cookies from before this release, including any opened
  while a server with no token accepted every input. Provider sessions are unchanged.
- **A retired generated token can't come back.** A start with a configured token deletes
  `admin-token.json`, so removing the configured token later produces a new token rather than
  reviving the old one, which may have been replaced because it leaked.
- **Header parsing.** `Authorization: Bearer` is matched in any case, and `X-Admin-Token` is
  read when the Authorization header carries another scheme, for example a proxy's Basic
  credentials. A bare token in Authorization still works.
- A blank `ADJUTANT_ADMIN_TOKEN` no longer hides `Adjutant:AdminToken` from appsettings.json.
- Removing a configured token while the server is running leaves no token until the next start,
  which generates one. Until then the admin API refuses everything. The startup log says so.

## Upgrade notes

- **Existing admin-token console sessions end** at their next request, because they carry no
  token binding. Sign in again with the token.
- **docker-compose users who never set `ADJUTANT_ADMIN_TOKEN`** were running with the compose
  default `changeme`. With this compose file they get a generated token instead. Read it from
  `docker compose logs adjutant`. Scripts sending `changeme` get `401`. To keep a fixed token, set
  `ADJUTANT_ADMIN_TOKEN` in `.env`.

- Servers that set `ADJUTANT_ADMIN_TOKEN` (the k8s manifest does, from `adjutant-secrets`) are
  unaffected.
- A server that ran without a token, and so had an open admin API, generates one on its first start
  after the upgrade. Read it from the log. REST clients that called `/admin/*` without credentials
  now get `401`.
- A server that ran without a token, with a provider configured, outside Development, didn't offer
  the token form. Now it offers the form (accepting only the real token) until the first provider
  sign-in, the same break-glass rule as servers with a configured token.

## Tests

- `AdminTokenStoreTests`: generation, hash-only storage, survival across restarts, configured
  token wins (and the stored one stops working), blank counts as unset, malformed file replaced.
- `AdminTokenBootstrapIntegrationTests`: against the real server with no token configured,
  `GET`/`POST /admin/accounts` return 401 and write nothing, and the console rejects an arbitrary
  token. The stored token works as a Bearer token, through `X-Admin-Token` and on the login page,
  and a configured token overrides it. The first two tests fail against the old code.
- `AdminFunnelIntegrationTests`: the no-token case now expects 401 inside the tailnet as well.
