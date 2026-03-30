# AGENTS.md

## Project Snapshot

- `subscriptiond` is a small ASP.NET Core MVC app for anonymous YouTube subscription lists.
- The only solution project is [`youtubed`](youtubed), targeting `.NET 10`.
- Users do not sign in. Access is via secret list URLs shaped like `/{token}/list/{id}`.
- Persistence is SQL Server via Dapper.
- The app stores shared channel/video cache data so multiple lists can reuse the same channel metadata.

## Architecture

- Entry point, DI, and middleware pipeline: [`youtubed/Program.cs`](youtubed/Program.cs)
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

- Local development runs against SQL Server Express LocalDB 2019 and expects `ConnectionStrings:Main` in [`youtubed/appsettings.Development.json`](youtubed/appsettings.Development.json).
- YouTube credentials are bound from `Youtube` configuration into `YoutubeOptions`.
- The app relies on two hosted services for freshness and cleanup; list pages may auto-refresh while stale channels are being updated.
- The repo currently includes committed build output under `youtubed/bin` and `youtubed/obj`.

## Constraints And Gotchas

- The runtime is on `.NET 10`, but the frontend stack still mixes older libraries and tooling such as Bootstrap 3, jQuery validation, Bower, and BuildBundlerMinifier.
- Routing now uses endpoint routing with controller attributes mapped via `MapControllers()`, so preserve the existing attribute route templates when changing URLs.
- SQL is SQL Server-specific and uses `MERGE` plus TVPs.
- YouTube URL support is limited to `/channel/...`, `/user/...`, and video URLs used as a fallback for vanity channels.
- `YoutubeService` currently hardcodes video duration to 5 minutes instead of loading real durations.
- Automated coverage now includes controller/unit tests, repository and service LocalDB integration tests, and lightweight route-level integration tests for app startup and URL compatibility.

## Safe Assumptions For Future Sessions

- Prefer small, local changes over sweeping rewrites unless explicitly requested.
- Treat `bin/`, `obj/`, and generated publish output as noise unless the task is about build or deployment.
- Preserve the anonymous secret-link model unless the user explicitly asks for authentication or accounts.
- If changing persistence, inspect both Dapper SQL and the schema together; behavior is encoded in both places.
- Check for user changes before editing; the worktree may already contain unrelated files.

## Commit Convention

- Commit messages should end with a Git trailer in this exact form: `Co-Authored-By: Codex %MODEL_NAME%`
- `%MODEL_NAME%` should be the full model name, for example `GPT-5.4`, not a shortened variant like `GPT-5`.
- Before creating or amending a commit, assess the change severity and update [`youtubed/youtubed.csproj`](youtubed/youtubed.csproj) `AssemblyVersion` in the same change when the shipped code meaningfully changes.
- `AssemblyVersion` must stay in `major.minor.build.revision` format.
- Increment `major` for breaking changes or major platform/application shifts, then reset `minor`, `build`, and `revision` to `0`.
- Increment `minor` for backward-compatible feature additions, then reset `build` and `revision` to `0`.
- Increment `build` for backward-compatible fixes, refactors, or internal improvements, then reset `revision` to `0`.
- Increment `revision` only for very small corrective follow-ups or repackaging-level changes when `major`, `minor`, and `build` should stay the same.

## Tips for Agents

- `rg` is not available in this environment; prefer PowerShell-native search commands like `Get-ChildItem`, `Select-String`, and `Get-Content` when locating files or text.

- Long-running tooling (tests, docker compose, migrations, etc.) must always be invoked with sensible timeouts or in non-interactive batch mode. Never leave a shell command waiting indefinitely—prefer explicit timeouts, scripted runs, or log polling after the command exits.
- The `dotnet` CLI will need network access and inside your sandbox you always have to run those commands with `with_escalated_permissions: true` on the `shell` tool call and include a one-sentence justification (e.g., "Need network access for npm install/build").
- Ensure to include the `with_escalated_permissions` for all builds, restores, migrations, installs, tests, etc where network access is required otherwise the command will hang.
- Git commands that write to the repository, such as `git add`, `git commit`, and similar index or ref updates, should also be run with elevated permissions in this environment.
- Accessing SQL Server Express LocalDB from this environment requires elevated permissions; LocalDB instance inspection and SQL connectivity checks will fail inside the sandbox even when the instance is running.
- When changing SQL code, always re-run the LocalDB integration tests with `YOUTUBED_RUN_LOCALDB_TESTS=true` before wrapping up.
