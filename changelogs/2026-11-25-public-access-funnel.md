# Public access through Tailscale Funnel

**Date:** 2026-11-25
**Issue:** [#81](https://github.com/MarimerLLC/adjutant/issues/81), phase 5

The last phase of making the HTTP server safe to expose to the internet. Phases 1–4 (MCP API
keys, admin OIDC sign-in, the claim flow, session hardening, the Settings and keys pages) shipped
in #82. The anonymous-surface trim shipped in #83, and MCP OAuth (phase 6) in #117 and #119.

## The admin token is tailnet-only

Tailscale's proxy marks requests that arrived over Funnel with a `Tailscale-Funnel-Request`
header. On those requests the break-glass admin token is never accepted (`FunnelRequest`):

- `Login.razor` hides the token field, shows "available only from inside the tailnet", and its
  handler refuses a token login posted anyway. In practice Blazor already rejects such a post
  with a 400, because the form it names isn't rendered.
- `AdminAuthMiddleware` treats a console session established with the token as signed out on
  `/admin/ui` and on the mailbox `google/start` path, so a token-session cookie can't be replayed
  from the internet. The host name, and so the cookie, is the same inside and outside the
  tailnet.
- The REST API under `/admin/*` returns `401` over Funnel, even with the correct token, and even
  with no token configured, where it would otherwise be open.

Provider sign-in, MCP API keys and MCP OAuth are unaffected. The header only removes access, so a
forged one grants nothing. If the proxy ever stopped sending it, behaviour falls back to the
pre-Funnel rules, which is why the runbook's verification step checks for it.

**Why:** the live server is exposed before it is necessarily claimed. Until the first provider
sign-in, token login is on by design (#125), and a single shared secret should not be one
password away from the internet.

## Manifest

- `tailscale.com/funnel: "true"` on the `adjutant-tailscale` Ingress, with the prerequisites in a
  comment. Applying the manifest is what publishes the server.
- `ADJUTANT_Adjutant__ExternalBaseUrl` set to the Ingress host, so a fresh data volume can't
  come up with a missing or `http://` base URL.

## Docs

- New `docs/PUBLIC-ACCESS.md`: what Funnel publishes, the pre-flight checklist (including the
  `funnel` node attribute in the tailnet policy), how to verify from outside the tailnet, and
  how to turn it off.
- `docs/GOOGLE-SETUP.md` and `docs/M365-SETUP.md`: walkthroughs for the *admin console* sign-in
  client, separate from mailbox access.
- `docs/security.md`, `docs/configuration.md`, `docs/DEPLOYMENT.md`, `docs/INSTALLATION.md` and
  `README.md`: the Funnel rule, plus links to the runbook.

## Still unverified

- That the Tailscale Kubernetes operator's Ingress proxy passes `Tailscale-Funnel-Request`
  through to the pod. The runbook's REST check confirms it.
- Whether Google accepts a `<tailnet>.ts.net` redirect URI on a *published* app. Testing mode is
  the documented route.
