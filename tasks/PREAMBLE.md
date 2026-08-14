# Task Execution Rules

## Authoritative Design

Before implementing a task, read:

- [`../docs/architecture.md`](../docs/architecture.md);
- [`../docs/cosmos-data-model.md`](../docs/cosmos-data-model.md);
- [`../docs/migration-and-operations.md`](../docs/migration-and-operations.md);
- the repository `AGENTS.md`.

If a task conflicts with those documents, stop and update the design explicitly before coding.
Do not silently reintroduce the discarded recovery-ledger or embedded-projection architecture.

## Status And Dependencies

Every task has one status: `Not Started`, `In Progress`, or `Completed`.

Before changing code:

1. confirm every dependency is `Completed`;
2. set the task to `In Progress`;
3. inspect the current implementation because earlier task summaries may not capture follow-ups.

Complete one task at a time. Do not opportunistically implement later tasks unless the current task
cannot compile or validate without a narrowly scoped dependency adjustment.

## Implementation Discipline

- Keep SQL and Cosmos storage details behind provider-specific persistence code.
- Preserve the anonymous secret-link model and existing controller route templates.
- Reuse existing domain models, services, mappers, and test infrastructure when they still fit.
- Prefer deletion over compatibility shims for unshipped Cosmos recovery behavior.
- Do not preserve old recovery document shapes or tests as a second supported mode.
- Treat the one-instance, request-driven refresh model as a product constraint.
- Use ETags with one reread/reapply attempt for Cosmos conflicts.
- Never log tokens, share passwords, keys, connection strings, document bodies, or raw diagnostics.
- Update `AssemblyVersion` in the eventual commit for each meaningful shipped code change, following
  `AGENTS.md`.

## Validation

Run validation sequentially after a successful build:

1. tests excluding LocalDB and Cosmos;
2. opted-in LocalDB integration tests when SQL or shared behavior changed;
3. opted-in Cosmos emulator tests when Cosmos behavior changed.

Also run `git diff --check`. Run formatting and vulnerability checks when required by `AGENTS.md` or
when preparing deployment. Never report a skipped, unavailable, or failed check as passing.

Cosmos emulator tests should prove visible behavior and the documented request shape. Add genuine
concurrency tests only for supported competing requests such as ETag list mutation or share-link
consumption; do not rebuild failure matrices for deliberately absent distributed invariants.

## Completion

A task can be marked `Completed` only when:

- its scoped implementation and tests are committed together;
- every required check passed, or the user explicitly changed the task's validation requirement;
- its implementation summary records material behavior, deletions, validation evidence, and any
  accepted constraint;
- no temporary compatibility path or unexplained TODO remains.

Production provisioning, deployment, and cutover require explicit user authorization even when a
task describing them is ready to execute.
