# Task 05: Consolidate Cosmos operation execution and telemetry

Implement this task after Task 04. Consolidate the repeated Cosmos SDK execution, timing, exception logging, and request telemetry code used by the list, channel, and share-link repositories. The goal is one small persistence-internal mechanism, not a generic repository framework.

## Required changes

- Identify the duplicated execution wrappers and telemetry paths in `CosmosRepositoryClient`, `CosmosShareLinkRepository`, and any other Cosmos persistence classes.
- Introduce or reshape one internal Cosmos operation executor/client that consistently handles:
  - elapsed-time measurement;
  - success and `CosmosException` telemetry;
  - operation and container names;
  - status code, request charge, and retry count;
  - point-read not-found handling where requested by the caller;
  - feed-page telemetry for queries.
- Reuse that mechanism from list, channel, and share-link persistence code.
- Keep domain-specific conflict, idempotency, retry, and mapping decisions in their repositories. The shared mechanism must not decide application semantics.
- Preserve cancellation tokens throughout SDK calls; do not replace them with `CancellationToken.None` where a caller supplies a token.
- Remove obsolete wrappers and duplicated logging helpers after all callers have migrated.

## Constraints

- Keep the abstraction internal to `Persistence/Cosmos`.
- Do not build a generic CRUD repository, unit of work, reflection-based mapper, or expression/query framework.
- Do not hide Cosmos request shape in a way that makes point reads, `ReadMany`, queries, ETags, or partition keys difficult to review.
- Preserve current retry limits and exception behavior.
- Never log document bodies, list IDs, channel IDs, share passwords, tokens, connection strings, partition-key values, or raw Cosmos diagnostics.

## Acceptance criteria

- Repositories no longer contain near-identical stopwatch/try/catch/request-telemetry implementations.
- All supported Cosmos operations emit the existing actionable telemetry fields consistently.
- Retry counts accurately distinguish initial requests from the one permitted optimistic-concurrency retry.
- Expected not-found and conflict handling remains explicit at the repository call site.
- Tests cover successful operations, Cosmos failures, feed operations, retry telemetry, and secret-safe logging.
- Request counts and RU observations remain available to release-envelope tests.

## Validation

Because this changes Cosmos plumbing and observability, run validation sequentially: build, tests excluding Cosmos, then the opted-in Cosmos emulator suite with `YOUTUBED_RUN_COSMOS_TESTS=true`. Verify representative request counts and RU telemetry as well as failure logging. Report unavailable or skipped checks as unverified.
