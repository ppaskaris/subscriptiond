# AGENTS.md

## Project Snapshot
- `subscriptiond` is a small ASP.NET Core MVC app for anonymous YouTube subscription lists.
- The only solution project is [`youtubed`](youtubed), targeting `.NET Core 3.1`.
- Users do not sign in. Access is via secret list URLs shaped like `/{token}/list/{id}`.
- Persistence is SQL Server via Dapper.
- The app stores shared channel/video cache data so multiple lists can reuse the same channel metadata.

## Architecture
- Entry point and DI: [`youtubed/Program.cs`](youtubed/Program.cs), [`youtubed/Startup.cs`](youtubed/Startup.cs)
- Web layer: [`youtubed/Controllers/HomeController.cs`](youtubed/Controllers/HomeController.cs), [`youtubed/Controllers/ListController.cs`](youtubed/Controllers/ListController.cs)
- Data access: [`youtubed/Services/ListService.cs`](youtubed/Services/ListService.cs), [`youtubed/Services/ChannelService.cs`](youtubed/Services/ChannelService.cs), [`youtubed/Services/ChannelVideoService.cs`](youtubed/Services/ChannelVideoService.cs)
- YouTube API integration: [`youtubed/Services/YoutubeService.cs`](youtubed/Services/YoutubeService.cs)
- Background jobs: [`youtubed/Services/MaintenanceHostedService.cs`](youtubed/Services/MaintenanceHostedService.cs), [`youtubed/Services/UpdateChannelHostedService.cs`](youtubed/Services/UpdateChannelHostedService.cs)
- SQL schema: [`youtubed/Schema.sql`](youtubed/Schema.sql)

## Request Flow
- `GET /` shows the landing page and list creation entry point.
- `POST /create-list` creates a `List` with a random 40-byte token and redirects to the secret list URL.
- `GET /{token}/list/{id}` refreshes list expiry, counts stale channels, loads cached videos, and renders the list.
- `POST /{token}/list/{id}/add-channel` validates a YouTube URL, resolves the channel, and upserts the list-channel mapping.
- `POST /{token}/list/{id}/remove-channel` removes the mapping only; channel cleanup is deferred to maintenance.
- `POST /{token}/list/{id}/edit` renames the list.
- `POST /{token}/list/{id}/delete` deletes the list immediately.

## Data Model
- `List`: `Id`, `Token`, `Title`, `ExpiredAfter`
- `Channel`: cached channel metadata plus `StaleAfter` and `VisibleAfter`
- `ChannelVideo`: cached recent videos per channel
- `ListChannel`: many-to-many join
- `ChannelVideoType`: SQL Server table-valued type used for batch upserts

## Operational Notes
- Local development expects SQL Server and `ConnectionStrings:Main` in [`youtubed/appsettings.Development.json`](youtubed/appsettings.Development.json).
- YouTube credentials are bound from `Youtube` configuration into `YoutubeOptions`.
- The app relies on two hosted services for freshness and cleanup; list pages may auto-refresh while stale channels are being updated.
- The repo currently includes committed build output under `youtubed/bin` and `youtubed/obj`.

## Constraints And Gotchas
- Framework and packages are old: `.NET Core 3.1`, Bootstrap 3, jQuery validation, Bower, BuildBundlerMinifier.
- Routing still uses `UseMvc()` with endpoint routing disabled.
- SQL is SQL Server-specific and uses `MERGE` plus TVPs.
- YouTube URL support is limited to `/channel/...`, `/user/...`, and video URLs used as a fallback for vanity channels.
- `YoutubeService` currently hardcodes video duration to 5 minutes instead of loading real durations.
- There are no automated tests in the repo.

## Safe Assumptions For Future Sessions
- Prefer small, local changes over sweeping rewrites unless explicitly requested.
- Treat `bin/`, `obj/`, and generated publish output as noise unless the task is about build or deployment.
- Preserve the anonymous secret-link model unless the user explicitly asks for authentication or accounts.
- If changing persistence, inspect both Dapper SQL and the schema together; behavior is encoded in both places.
- Check for user changes before editing; the worktree may already contain unrelated files.
