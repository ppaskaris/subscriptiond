# Task 04: Move share consumption orchestration to the service layer

Implement this task after Task 03. Make the share-link repository operate only on share-link persistence. Move the cross-aggregate share-consumption workflow into `ShareLinkService` without weakening its single-use or secret-protection guarantees.

## Required changes

- Replace the repository-level `ConsumeAsync` operation that reads both share-link and list containers and returns a list token with focused share-link operations suitable for service orchestration.
- Have `ShareLinkService` coordinate `IShareLinkRepository` and `IListRepository` to:
  1. read and validate the share link;
  2. reject missing, expired, or already-used links;
  3. read the target list;
  4. mark the share link used with ETag-based optimistic concurrency;
  5. return the list ID and token only after the used-state write succeeds.
- Keep Cosmos ETags internal to the persistence layer. Design a storage-agnostic repository contract that can express conditional consumption without exposing Cosmos SDK types or persistence documents.
- Preserve one retry after an optimistic-concurrency conflict only where rereading and reapplying is semantically valid. A competing successful consumption must produce failure and must never reveal the list token.
- Remove cross-container list reads and token handling from `CosmosShareLinkRepository`.
- Update service, repository contract, concurrency, controller, and integration tests accordingly.

## Failure semantics to preserve

- A missing or expired share link is not consumable.
- A link pointing at a missing list is not marked used and reveals no token.
- If marking the link used fails, no token is returned.
- Two genuinely concurrent consumers cannot both succeed.
- Once consumption succeeds, later attempts fail even though Cosmos TTL may not yet have physically deleted the link.
- Tokens, connection strings, and passwords must not appear in logs or exception messages.

## Constraints

- Preserve the anonymous secret-link model and all existing routes.
- Do not create a transaction, recovery, workflow, lease, or outbox document.
- Do not expose ETags, partition keys, Cosmos response types, or Cosmos documents through domain models or repository interfaces.
- Do not store the list token in a share-link document.

## Acceptance criteria

- `CosmosShareLinkRepository` only accesses the `shareLinks` container.
- Cross-aggregate orchestration is visible and testable in `ShareLinkService`.
- The repository contract remains storage-agnostic while enforcing conditional single-use consumption.
- Tests cover failure after each durable side effect, retry/restart behavior where applicable, a missing target list, and genuine concurrent consumption against the Cosmos emulator.
- The successful result is returned only after the conditional used-state write has completed.

## Validation

This is a cross-container workflow change. Run validation sequentially: build, tests excluding Cosmos, then the opted-in Cosmos emulator suite with `YOUTUBED_RUN_COSMOS_TESTS=true`. The emulator suite must include genuine concurrent execution; mocked ETag conflicts alone are insufficient. Also retain applicable TTL lifecycle coverage. Report unavailable or skipped checks as unverified.
