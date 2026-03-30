# ROADMAP.md

## Pending Tasks (in priority order)

3. Upgrade the app from `.NET Core 3.1` to a supported LTS release.
   Why: the current runtime is long out of support, which increases security and maintenance risk across the whole stack.
   When: by this point the app should be easier to validate, making the framework upgrade less risky.

4. Move from legacy MVC startup patterns to the modern hosting model.
   Why: [`youtubed/Startup.cs`](youtubed/Startup.cs) still disables endpoint routing and uses `UseMvc()`, which makes future framework upgrades harder.
   When: this follows naturally after the runtime upgrade and aligns the app with current ASP.NET conventions.

5. Replace Bower and BuildBundlerMinifier with a current asset pipeline.
   Why: the frontend toolchain is obsolete and creates avoidable friction for dependency updates and local setup.
   When: once the backend platform is modernized, the frontend toolchain can be updated with less unrelated churn.

6. Upgrade Bootstrap and client-side libraries or reduce frontend dependency surface.
   Why: Bootstrap 3 and old jQuery validation packages are dated and likely to lag on compatibility, accessibility, and security maintenance.
   When: this builds on the new asset pipeline and is easier once frontend dependency management is modernized.

7. Clean the repository layout and deployment artifacts.
   Why: committed `bin/`, `obj/`, and publish output add noise, increase context load, and make code review and navigation less pleasant.
   When: cleanup is more useful after the major platform and tooling changes have landed.

8. Expand YouTube URL parsing to support newer channel handle formats and fetch real video durations.
   Why: current support is narrow and [`youtubed/Services/YoutubeService.cs`](youtubed/Services/YoutubeService.cs) hardcodes a placeholder duration.
   When: this is a product enhancement rather than foundational modernization, so it can come after the platform is in a healthier state.

## Completed Tasks

1. Add automated tests around list lifecycle and channel refresh behavior. (Completed)
   Why: there are no tests, and the most important business behavior currently lives in controllers plus SQL-heavy services.
   When: upgrade and refactor work will be safer once core flows have regression coverage.
   Progress: Added an xUnit regression suite for `HomeController` and `ListController`, plus opt-in LocalDB integration tests for `ListService`, `ChannelService`, and `ChannelVideoService` covering list lifecycle, stale-channel claiming, orphan cleanup, and channel video refresh behavior against the real schema.

2. Introduce a cleaner persistence boundary and integration-test the SQL. (Completed)
   Why: important logic is split between Dapper queries and [`youtubed/Schema.sql`](youtubed/Schema.sql), so refactors are risky without stronger coverage and clearer repository boundaries.
   When: persistence logic is central to the app, and tightening this layer reduces risk before platform and concurrency changes.
   Progress: Extracted Dapper/SQL code from the application services into a dedicated `youtubed/Persistence` layer, kept the service interfaces focused on orchestration, and added repository-oriented LocalDB integration tests alongside the existing service-level SQL coverage.

3. Revisit SQL Server-specific `MERGE` usage and background-job concurrency behavior. (Completed)
   Why: `MERGE` can be brittle, and the stale-channel claiming flow should be reviewed before scaling or changing job execution patterns.
   When: this is core behavioral infrastructure that should be stabilized before broader modernization changes.
   Progress: Replaced the remaining `MERGE` usage in channel discovery and video refresh with explicit transactional SQL, tightened stale-channel claiming so only one worker can lease a channel at a time without relying on isolation-level-specific hints, documented the lease behavior in the refresh path, and reran the full LocalDB integration suite to verify the SQL-backed coordination behavior.
