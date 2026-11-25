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

Side effect: the shipped `Kestrel` endpoint (`http://+:8080`) now stays in effect after the
operator's file appears. Before, it silently dropped out on the second start. The Docker image sets
`ASPNETCORE_URLS=http://+:8080` as well, so the listening port doesn't change.

A Kestrel endpoint takes precedence over `ASPNETCORE_URLS`, which therefore no longer changes the
port on any start; before, it applied from the second start on. Change the port with
`Kestrel:Endpoints:Http:Url` in the data-directory file, or with
`ADJUTANT_Kestrel__Endpoints__Http__Url`. A manual run confirmed `ASPNETCORE_URLS` is overridden.
No shipped deployment sets it to anything but 8080.

## Tests

`FirstRunConfigurationTests`: an account written through `IAccountConfigurationService` on a fresh
data directory reaches `IOptionsMonitor<AdjutantConfiguration>` and `IAccountRegistry` through the
file watcher (no explicit `Reload`). It fails against the old loading. Also covers layering
precedence and the case where both paths name the same file.
