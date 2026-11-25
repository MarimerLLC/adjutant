# Public Access (Tailscale Funnel)

This runbook covers exposing the Kubernetes deployment in [`k8s/`](../k8s/) to the public
internet through a Tailscale Funnel endpoint, so that MCP clients and administrators outside the
tailnet can reach it.

Funnel publishes the **whole origin**. There is no per-path filter, so everything below becomes
internet-reachable at once:

| Path | Protection over Funnel |
|---|---|
| `/` (MCP), `/attachments/*` | MCP API key or MCP OAuth token |
| `/connect/*`, `/.well-known/oauth-*` | MCP OAuth; offered only while an admin sign-in provider is configured |
| `/admin/ui/*` (console) | Provider sign-in (Google or Microsoft) on the allow-list. **Admin-token login is refused.** |
| `/admin/*` (REST API) | **Refused** (`401`), with or without the admin token |
| `/health`, `/health/ready` | Anonymous; status and timestamp only |
| `/openapi`, `/scalar/v1` | Not mapped outside Development (`404`) |

The admin token stays usable from inside the tailnet, which is what keeps it a break-glass path
rather than a shared password on the internet. See
[Security](security.md#requests-from-the-public-internet-tailscale-funnel).

## Before enabling Funnel

Work through these from inside the tailnet. Don't enable Funnel until every one is true.

1. **MCP keys are enforced.** `Adjutant:Mcp:RequireApiKey` is `true` (the default) and every
   client that calls the server has its own key under **MCP Access**.
2. **`ExternalBaseUrl` is the HTTPS public origin.** For the manifest's Ingress that is
   `https://adjutant.<tailnet>.ts.net`. It is set by `ADJUTANT_Adjutant__ExternalBaseUrl` in
   `k8s/deployment.yaml` and must match `tailscale.com/hostname`. The server refuses to start
   with a non-loopback `http://` value while keys are enforced.
3. **A sign-in provider is configured.** Set one up from **Settings → Sign-in providers**,
   signed in with the admin token. See the [Google](GOOGLE-SETUP.md#admin-console-sign-in-client-http-server)
   or [Microsoft Entra](M365-SETUP.md#admin-console-sign-in-app-http-server) walkthrough.
4. **The server is claimed.** Sign in with the provider and enter the claim code from the startup
   log or `/app/data/admin-claim-code.txt`. Afterwards `/app/data/admin-users.json` exists and
   the login page no longer shows the token field:

   ```bash
   kubectl -n adjutant exec deploy/adjutant -- ls /app/data/admin-users.json
   ```

5. **The running image refuses the admin token over Funnel.** The Funnel checks in this runbook
   need an image built from a commit that includes `FunnelRequest`. Images before that accept
   the token from anywhere.
6. **The tailnet policy allows Funnel for the proxy's tag.** The Ingress proxy runs as
   `tag:k8s-operator` (the `tailscale.com/tags` annotation). The tailnet policy file needs:

   ```json
   "nodeAttrs": [
     { "target": ["tag:k8s-operator"], "attr": ["funnel"] }
   ]
   ```

   HTTPS certificates must also be enabled for the tailnet. They already are if
   `https://adjutant.<tailnet>.ts.net` works inside it.

## Enabling it

Funnel is switched on by the `tailscale.com/funnel: "true"` annotation on the
`adjutant-tailscale` Ingress in `k8s/deployment.yaml`. Applying the manifest is what flips it,
so check what will change first:

```bash
MSYS_NO_PATHCONV=1 kubectl diff -f k8s/deployment.yaml
MSYS_NO_PATHCONV=1 kubectl apply -f k8s/deployment.yaml
```

Funnel listens on 443 and the Ingress already forwards to the service on 8080, so no port
changes are needed.

## Verifying from outside the tailnet

Run these from a machine that is **not** on the tailnet, such as a phone hotspot or a cloud
shell. Inside the tailnet the name resolves to the tailnet address and the requests never go
through Funnel.

```bash
BASE=https://adjutant.<tailnet>.ts.net

# MCP endpoint: 401 with a resource_metadata challenge (or without it, if no provider is configured)
curl -si -X POST "$BASE/" | head -n 20

# Health: 200, status and timestamp only
curl -s "$BASE/health/ready"

# API docs: 404
curl -s -o /dev/null -w '%{http_code}\n' "$BASE/scalar/v1"

# Login page: provider buttons, no token field, and the "only from inside the tailnet" note
curl -s "$BASE/admin/ui/login" | grep -E 'Sign in with|id="token"|inside the tailnet'

# REST API with the real admin token: 401 "available only from inside the tailnet"
curl -s -H "Authorization: Bearer $ADMIN_TOKEN" "$BASE/admin/accounts"
```

The last check also proves that Tailscale's `Tailscale-Funnel-Request` header reaches the pod.
If it returns the account list instead, the header is not arriving and the admin token is
accepted from the internet. **Turn Funnel back off** (see below) until that is resolved.

Then check the log line the refused request wrote:

```bash
kubectl -n adjutant logs deploy/adjutant | grep 'over Tailscale Funnel'
```

The address it names should be your outside machine's public IP. Sign-in rate limits are
partitioned by that address. If every Funnel request shows the same address instead (a
Tailscale relay), all internet users share one 10-per-minute sign-in bucket. That is a nuisance
rather than a bypass, but worth knowing.

Finally, sign in to the console with the provider from outside the tailnet, and connect one MCP
client using either a key or OAuth.

## Attachments

Funnel traffic is relayed through Tailscale's infrastructure, so it can be noticeably slower than
tailnet or in-cluster traffic. Clients that move large attachments should use the in-cluster
service or the tailnet address where they can. Measure an upload and a download through Funnel
before depending on it.

## Turning it off

Remove the `tailscale.com/funnel` annotation (or set it to `"false"`) and apply the manifest
again. The server stays reachable inside the tailnet. Nothing on the data volume changes, and
keys, OAuth clients and console users are kept.

Keep in mind that while the annotation is in `k8s/deployment.yaml`, **any** apply of that file
re-publishes the server, including a routine image bump.
