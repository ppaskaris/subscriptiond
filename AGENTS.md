# AGENTS.md

## Project-Specific Guidance

- Preserve the anonymous secret-link model unless the user explicitly asks for authentication or accounts.
- Treat Git-ignored files as noise when searching or reviewing unless the task is specifically about build or deployment output.
- Prefer reusing existing utilities and helper functions instead of defining new local helpers or duplicating logic.
- If changing SQL persistence behavior, inspect both Dapper SQL and [`youtubed/Schema.sql`](youtubed/Schema.sql); behavior is encoded in both places.
- If changing multi-persistence or Cosmos migration behavior, consult the relevant design docs in [`docs/`](docs/).
- Keep domain models and repository interfaces storage-agnostic. SQL rows and Cosmos documents should stay inside provider-specific persistence layers.
- SQL remains normalized and may compute read models with joins. Cosmos may denormalize documents for RU efficiency and reshape them in the persistence layer.
- For Cosmos-targeted work, prefer point reads, bounded documents, TTL for lifecycle cleanup, narrow indexing, and ETag-based optimistic concurrency. Use one retry after an optimistic-concurrency conflict, then throw unless a task says otherwise.
- Preserve existing controller attribute route templates when changing URLs.
- Be careful with frontend changes: the app still mixes older tooling and libraries, including Bootstrap 3, jQuery validation, Bower, and BuildBundlerMinifier.
- SQL in this repo is SQL Server-specific and uses patterns such as `MERGE` and TVPs; avoid database-agnostic rewrites unless explicitly requested.
- SQL Server migration scripts must be rerunnable/idempotent. Guard object creation, column additions, constraints, and data backfills so re-running a migration does not fail or duplicate data.
- In SQL Server migration scripts, do not statically reference a column later in the same batch after conditionally adding it; SQL Server can bind column names before the `ALTER TABLE` runs. Use a separate batch or dynamic SQL such as `sp_executesql` for the follow-up `UPDATE`/`ALTER COLUMN` statements.
- If changing SQL code, re-run the LocalDB integration tests with `YOUTUBED_RUN_LOCALDB_TESTS=true` before finishing.

## Environment Notes

- `rg` is not available here. Use PowerShell-native search commands, and scope repo searches with `git ls-files --cached --others --exclude-standard` so ignored files stay excluded.
- Run `dotnet`, LocalDB access, `gh`, and Git commands that write to the repository with elevated permissions in this environment.
- Run validation sequentially after the build; do not overlap build and test execution because this repo can hit flaky testhost/file-copy races.
- If changing Cosmos provider code, run Cosmos emulator tests with `YOUTUBED_RUN_COSMOS_TESTS=true` when the emulator is available.

## Commit Rules

- When a change fixes a GitHub issue, include a closing line such as `Closes #12` in the commit body.
- When creating or amending a commit for a meaningful shipped code change, update [`youtubed/youtubed.csproj`](youtubed/youtubed.csproj) `AssemblyVersion` in the same change.
- Keep `AssemblyVersion` in `major.minor.build.revision` format.
- Increment `major` for breaking changes, resetting `minor`, `build`, and `revision` to `0`.
- Increment `minor` for backward-compatible features, resetting `build` and `revision` to `0`.
- Increment `build` for backward-compatible fixes, refactors, or internal improvements, resetting `revision` to `0`.
- Increment `revision` only for very small corrective follow-ups or repackaging-level changes.
