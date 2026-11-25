# First-run "next steps" in the startup log

**Date:** 2026-11-25
**Issue:** [#132](https://github.com/MarimerLLC/adjutant/issues/132)

A first start printed the MCP key and claim-code banners (and, since #131, the admin token) but
not what to do with them. The account registry's "no accounts" warning only appeared on the first
tool call, and it pointed at the CLI.

## Change

- **`FirstRunGuide.LogFirstRunGuide`** (new, called after `app.Start()`): while the account
  registry is empty, it logs a next-steps block. Once an account exists it logs nothing, and it
  repeats on every start until then, since setup may span restarts.

  ```
  ========================================================================
  Adjutant is running, but no accounts are configured yet. Next steps:
    1. Open http://<host>:8080/admin/ui and sign in with the admin token from the first-start banner (...)
    2. Add an email/calendar account (http://<host>:8080/admin/ui/accounts/add).
    3. Point your MCP client at http://<host>:8080/ with an MCP API key (...)
  Docs: https://github.com/MarimerLLC/adjutant/blob/main/docs/INSTALLATION.md#first-run-http-server
  ========================================================================
  ```

  - The address is `Adjutant:ExternalBaseUrl` when set. Otherwise it's the first listening address
    (HTTPS preferred), with a wildcard host shown as `<host>`. An `ExternalBaseUrl` that is not http(s) is ignored.
  - Step 1 follows the sign-in options in effect: the configured token (`ADJUTANT_ADMIN_TOKEN` or `Adjutant:AdminToken`) or
    the generated one, the configured providers, the claim code while unclaimed, or how to enable a
    sign-in method when none is.
  - Step 3 says no key is needed when `Adjutant:Mcp:RequireApiKey` is false.
- Resolving the registry at startup puts its own warning in the startup log. The warning now
  names the admin console as well as the CLI.
- docs/INSTALLATION.md: new "First run (HTTP server)" section, which the banner links to.

## Tests

`FirstRunGuideTests` covers each sign-in variant, the MCP step without key enforcement, and how the
base URL is resolved. Every `AdjutantApp` integration test also runs the guide on startup, since
none of them has accounts. A manual run against an empty data directory showed the full banner
sequence: admin token, MCP key, claim code, then next steps.
