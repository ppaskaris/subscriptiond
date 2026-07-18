# Task 021: Design Cosmos Consistency And Recovery

Status: Not Started

Depends On: 1400_enable_cosmos_provider_and_validate_end_to_end

## Goal

Define a production-safe, RU-bounded recovery model for list membership and channel reverse references across Cosmos containers.

## Scope

- Document source-of-truth and convergence invariants for list membership, `subscribedListIds`, `subscriptionCount`, orphan markers, and TTL.
- Enumerate failure points for add, remove, explicit list deletion, automatic list TTL deletion, projection refresh, and application restart.
- Select a durable recovery mechanism that can discover both dead reverse references and missing reverse references without an unbounded account scan.
- Cover active and unavailable channels, including channels that will never enter stale refresh lookahead.
- Define retry, compensation, poison-item, observability, and bounded-work behavior.
- Define how recovery behaves under concurrent add/remove, list renewal/deletion, projection writes, and multiple application instances.
- Record the decision in the relevant `docs/` design documents and split implementation responsibilities between Tasks 2110 and 2120.

## Out Of Scope

- Implementing the selected mechanism.
- Changing the anonymous secret-link model.
- Production data migration.

## Validation

- The design includes a failure matrix showing the expected converged state after every durable side effect can fail.
- The design proves that no successful list membership can remain permanently missing from canonical channel state.
- The design proves that expired/deleted lists cannot keep unavailable channels alive indefinitely.
- The design includes RU bounds, document bounds, restart behavior, and multi-instance behavior.
- Existing Cosmos schema, implementation-contract, and worker-state documents are updated consistently.

## Implementation Summary

Not implemented.
