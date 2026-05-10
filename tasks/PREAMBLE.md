# Task Prompt Preamble

Use this preamble when starting any implementation task from this folder.

## Project Objective

The project is migrating toward a multi-persistence architecture that can keep SQL Server as the current provider while adding an RU-efficient Azure Cosmos DB for NoSQL backend later.

The high-level strategy is SQL-first:

1. Refactor the application domain, read models, and worker behavior while SQL Server still backs the app.
2. Hide storage-specific details behind provider-neutral interfaces.
3. Keep SQL implementations working and covered by tests.
4. Add Cosmos implementations behind the same interfaces.
5. Use shared provider contract tests to prove SQL and Cosmos behavior match at the domain level.

The Cosmos target optimizes for free-tier quota usage. The common list page should become a cheap point read in Cosmos, while SQL can continue using normalized tables and selective joins. The domain layer should not know whether data is normalized or denormalized.

Preserve the anonymous secret-link model. Do not introduce accounts or authentication unless explicitly requested.

## Design Docs Index

Read only the docs relevant to the task, but use this index to know where to look.

- `docs/multi-persistence-system-design.md`
  - High-level architecture for SQL plus Cosmos providers.
  - Domain concepts, provider boundaries, shared ports, time abstraction, and contract-test strategy.

- `docs/implementation-contracts.md`
  - Sketches of the provider-neutral interfaces.
  - Conflict retry policy: one retry after the initial optimistic-concurrency failure, then throw.
  - Configuration knobs, worker logging expectations, and recommended early implementation order.

- `docs/pre-cosmos-application-behavior.md`
  - Application behavior changes to make before Cosmos exists.
  - List read models, stale channel count behavior, unavailable channel behavior, daily list renewal, URL lookup cache, and projection/read-model rules.

- `docs/worker-state-model.md`
  - Unified background worker design.
  - Worker state semantics, state diagram, batching, YouTube call flow, and cancellation rules.

- `docs/sql-schema-plan.md`
  - SQL schema changes for the SQL-first refactor.
  - Channel status fields, list renewal field, worker state table, `VisibleAfter` removal, and SQL expiration purger behavior.

- `docs/cosmos-schema-plan.md`
  - Target Cosmos containers, partition keys, document shapes, TTL usage, projection sizing, and indexing policy intent.

- `docs/cosmos-implementation-sketch.md`
  - How the Cosmos provider should implement list, channel, share link, projection, worker state, and expiration purger interfaces.
  - Cosmos emulator test expectations.

## Tasks Folder

Implementation tasks live in `tasks/NNNN_task_name.md`. Task IDs are fixed-width numeric ordering prefixes. New planned tasks usually advance by 100; splits should use available numbers between neighboring tasks, such as `0611_task_name.md` or `0612_task_name.md`.

The prefix is dependency order. Prefer completing lower-numbered tasks first, including inserted split tasks in numeric order, unless the current task explicitly says its dependencies are complete or the user asks otherwise.

Each task file uses this structure:

- `Status`
  - Use `Not Started`, `In Progress`, or `Completed`.

- `Depends On`
  - Lists prerequisite task ids/names.
  - Before implementing a task, inspect its dependencies and confirm they are completed or that the user intentionally wants to work out of order.

- `Goal`
  - The outcome the task should accomplish.

- `Scope`
  - Work that belongs in the task.

- `Out Of Scope`
  - Work to avoid even if nearby.

- `Validation`
  - Tests or checks to run before completing the task.

- `Implementation Summary`
  - Update this when completing the task.
  - Summarize what changed, important decisions made during implementation, and what validation passed or could not be run.

## Progress Tracking Rules

When starting a task:

1. Open the task file.
2. Open the relevant design docs from the index above.
3. Check dependency task files.
4. Set `Status: In Progress` before making code changes.

When finishing a task:

1. Run the validation listed in the task file, unless blocked.
2. Update `Status: Completed`.
3. Fill in `Implementation Summary`.
4. Mention any tests that could not be run and why.

If implementation changes the design:

1. Update the relevant docs in `docs/`.
2. Update the current task's `Implementation Summary`.
3. If future tasks are affected, update those task files too.

Keep task changes bite-sized. Do not opportunistically implement later tasks unless the user explicitly asks or the current task cannot be completed without moving a dependency forward.
