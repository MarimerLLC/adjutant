# Admin Console Is Interactive in the Docker Image

**Date:** 2026-11-25
**Version:** 2.2.1
**PR:** [#143](https://github.com/MarimerLLC/adjutant/pull/143)

---

## Summary

In every Docker image through 2.2.0, `/_framework/blazor.web.js` returned 404. The admin console
pages rendered, but nothing on them was interactive: checkboxes and buttons did nothing, and
forms couldn't be saved. `dotnet run` was unaffected.

## Cause

In .NET 10, `blazor.web.js` ships in the `Microsoft.AspNetCore.App.Internal.Assets` package.
Restore adds that package only when `RequiresAspNetWebAssets` is true, and the SDK sets that only
when the project's content includes `.razor` files. The Dockerfile restores from the `.csproj`
files alone (to cache the restore layer) and publishes with `--no-restore`, so the package never
arrived.

## What changed

- `Adjutant.HttpServer.csproj` sets `<RequiresAspNetWebAssets>true</RequiresAspNetWebAssets>`.
  The Dockerfile's cached restore layer is unchanged.
- CI has a new **Docker image** job. It builds the image and fails if
  `/app/wwwroot/_framework/blazor.web.js` is missing.
