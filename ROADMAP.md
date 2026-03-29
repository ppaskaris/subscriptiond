# ROADMAP.md

## Top 10 Modernization Tasks

1. Upgrade the app from `.NET Core 3.1` to a supported LTS release.
Reason: the current runtime is long out of support, which increases security and maintenance risk across the whole stack.

2. Move from legacy MVC startup patterns to the modern hosting model.
Reason: [`youtubed/Startup.cs`](youtubed/Startup.cs) still disables endpoint routing and uses `UseMvc()`, which makes future framework upgrades harder.

3. Replace Bower and BuildBundlerMinifier with a current asset pipeline.
Reason: the frontend toolchain is obsolete and creates avoidable friction for dependency updates and local setup.

4. Upgrade Bootstrap and client-side libraries or reduce frontend dependency surface.
Reason: Bootstrap 3 and old jQuery validation packages are dated and likely to lag on compatibility, accessibility, and security maintenance.

5. Add automated tests around list lifecycle and channel refresh behavior.
Reason: there are no tests, and the most important business behavior currently lives in controllers plus SQL-heavy services.

6. Introduce a cleaner persistence boundary and integration-test the SQL.
Reason: important logic is split between Dapper queries and [`youtubed/Schema.sql`](youtubed/Schema.sql), so refactors are risky without stronger coverage and clearer repository boundaries.

7. Revisit SQL Server-specific `MERGE` usage and background-job concurrency behavior.
Reason: `MERGE` can be brittle, and the stale-channel claiming flow should be reviewed before scaling or changing job execution patterns.

8. Expand YouTube URL parsing to support newer channel handle formats and fetch real video durations.
Reason: current support is narrow and [`youtubed/Services/YoutubeService.cs`](youtubed/Services/YoutubeService.cs) hardcodes a placeholder duration.

9. Clean the repository layout and deployment artifacts.
Reason: committed `bin/`, `obj/`, and publish output add noise, increase context load, and make code review and navigation less pleasant.

## Sequential Order

1. Add automated tests around list lifecycle and channel refresh behavior.
Why second: upgrade and refactor work will be safer once core flows have regression coverage.

2. Introduce a cleaner persistence boundary and integration-test the SQL.
Why third: persistence logic is central to the app, and tightening this layer reduces risk before platform and concurrency changes.

3. Revisit SQL Server-specific `MERGE` usage and background-job concurrency behavior.
Why fourth: this is core behavioral infrastructure that should be stabilized before broader modernization changes.

4. Upgrade the app from `.NET Core 3.1` to a supported LTS release.
Why fifth: by this point the app should be easier to validate, making the framework upgrade less risky.

5. Move from legacy MVC startup patterns to the modern hosting model.
Why sixth: this follows naturally after the runtime upgrade and aligns the app with current ASP.NET conventions.

6. Replace Bower and BuildBundlerMinifier with a current asset pipeline.
Why seventh: once the backend platform is modernized, the frontend toolchain can be updated with less unrelated churn.

7. Upgrade Bootstrap and client-side libraries or reduce frontend dependency surface.
Why eighth: this builds on the new asset pipeline and is easier once frontend dependency management is modernized.

8. Clean the repository layout and deployment artifacts.
Why ninth: cleanup is more useful after the major platform and tooling changes have landed.

9. Expand YouTube URL parsing to support newer channel handle formats and fetch real video durations.
Why tenth: this is a product enhancement rather than foundational modernization, so it can come after the platform is in a healthier state.
