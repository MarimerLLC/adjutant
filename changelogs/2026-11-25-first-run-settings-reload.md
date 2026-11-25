# First-run settings take effect without a restart

**Date:** 2026-11-25
**Issue:** [#130](https://github.com/MarimerLLC/adjutant/issues/130)

On a first start with an empty data directory, an account added in the admin console was written
to `<data dir>/appsettings.json` but didn't reach the running server until it restarted. The HTTP
server loaded only one settings file: the data-directory copy if it existed, otherwise the copy
shipped next to the binaries. On a fresh install it loaded and watched the shipped copy, while the
console wrote to a file that wasn't a configuration source.

## Change

`ServerConfigurationSources.AddSettingsFiles` (called from `Program.cs`) now always registers both
files, layered:

1. `<app dir>/appsettings.json`: shipped defaults, optional, not watched.
2. `<data dir>/appsettings.json`: operator settings, optional, watched with `reloadOnChange` even
   while it doesn't exist, so the file watcher picks up its creation.

Environment variables still apply last. The startup log names both files and whether each was found.

## Listening address

The shipped appsettings.json had a `Kestrel` endpoint, `http://+:8080`. Layered under the operator's
file, it would have applied on every start and overridden `ASPNETCORE_URLS`. That includes the
`localhost` URL in launchSettings.json, so `dotnet run` would have listened on every interface.
The endpoint is removed from the shipped file. Where the server listens:

| Setup | Before | Now |
|---|---|---|
| Docker image (`ASPNETCORE_URLS=http://+:8080`) | `+:8080` | `+:8080` |
| `dotnet run` (launchSettings `localhost:8080`) | first start `+:8080`, then `localhost:8080` | `localhost:8080` |
| Bare run, nothing set | first start `+:8080`, then `localhost:5000` | `localhost:8080` |
| `ASPNETCORE_HTTP_PORTS` | ignored (dropped with the default sources) | honored |
| `Kestrel:Endpoints` / `ADJUTANT_Kestrel__Endpoints__Http__Url` | honored | honored |

`ASPNETCORE_URLS`, `--urls` and `ASPNETCORE_HTTP(S)_PORTS` are read before the default
configuration sources are cleared. The two port settings are reapplied afterwards. When nothing
sets an address, `ServerConfigurationSources.DefaultUrlWhenUnconfigured` supplies
`http://localhost:8080`. Each row was checked against the built server.

## Tests

`FirstRunConfigurationTests`: an account written through `IAccountConfigurationService` on a fresh
data directory reaches `IOptionsMonitor<AdjutantConfiguration>` and `IAccountRegistry` through the
file watcher (no explicit `Reload`). It fails against the old loading. Also covers layering
precedence and the case where both paths name the same file.
