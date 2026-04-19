# AGENTS.md

## Project-Specific Guidance

- Preserve the anonymous secret-link model unless the user explicitly asks for authentication or accounts.
- Prefer small, local changes over sweeping rewrites unless explicitly requested.
- Treat Git-ignored files as noise when searching or reviewing unless the task is specifically about build or deployment output.
- If changing persistence behavior, inspect both Dapper SQL and [`youtubed/Schema.sql`](youtubed/Schema.sql); behavior is encoded in both places.
- Preserve existing controller attribute route templates when changing URLs.
- Be careful with frontend changes: the app still mixes older tooling and libraries, including Bootstrap 3, jQuery validation, Bower, and BuildBundlerMinifier.
- SQL in this repo is SQL Server-specific and uses patterns such as `MERGE` and TVPs; avoid database-agnostic rewrites unless explicitly requested.
- In SQL Server migration scripts, do not statically reference a column later in the same batch after conditionally adding it; SQL Server can bind column names before the `ALTER TABLE` runs. Use a separate batch or dynamic SQL such as `sp_executesql` for the follow-up `UPDATE`/`ALTER COLUMN` statements.
- If changing SQL code, re-run the LocalDB integration tests with `YOUTUBED_RUN_LOCALDB_TESTS=true` before finishing.

## Environment Notes

- `rg` is not available here. Use PowerShell-native search commands, and scope repo searches with `git ls-files --cached --others --exclude-standard` so ignored files stay excluded.
- Run `dotnet`, LocalDB access, `gh`, and Git commands that write to the repository with elevated permissions in this environment.
- Run validation sequentially after the build; do not overlap build and test execution because this repo can hit flaky testhost/file-copy races.

## Commit Rules

- When interpolating `%MODEL_NAME%`, use the full model name, for example `GPT-5.4`.
- Commit messages must end with `Co-Authored-By: Codex %MODEL_NAME%`.
- When a change fixes a GitHub issue, include a closing line such as `Closes #12` in the commit body.
- When creating or amending a commit for a meaningful shipped code change, update [`youtubed/youtubed.csproj`](youtubed/youtubed.csproj) `AssemblyVersion` in the same change.
- Keep `AssemblyVersion` in `major.minor.build.revision` format.
- Increment `major` for breaking changes, resetting `minor`, `build`, and `revision` to `0`.
- Increment `minor` for backward-compatible features, resetting `build` and `revision` to `0`.
- Increment `build` for backward-compatible fixes, refactors, or internal improvements, resetting `revision` to `0`.
- Increment `revision` only for very small corrective follow-ups or repackaging-level changes.

## GitHub Metadata

- When creating GitHub issues or pull requests, append `Created-By: Codex %MODEL_NAME%` at the end of the body text.
