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

9. Improve configuration and secret handling documentation.
Reason: the repo expects SQL Server and YouTube API credentials, but setup and deployment expectations are only partially discoverable from config files and source.

10. Clean the repository layout and deployment artifacts.
Reason: committed `bin/`, `obj/`, and publish output add noise, increase context load, and make code review and navigation less pleasant.

## Suggested Order
- Do `1`, `2`, `5`, and `9` first to reduce platform risk and make future changes safer.
- Do `3`, `4`, and `10` next to improve day-to-day development experience.
- Do `6`, `7`, and `8` after tests are in place, since those changes touch core behavior.
