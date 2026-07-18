# Task 020: Bound Cosmos List Projections

Status: Not Started

Depends On: 1400_enable_cosmos_provider_and_validate_end_to_end

## Goal

Guarantee that every Cosmos list document remains predictably bounded, RU-efficient, and safely below the Cosmos DB for NoSQL item-size limit while preserving the documented list-page behavior.

## Scope

- Implement the target-list-aware projection rule from `docs/cosmos-schema-plan.md`:
  - retain all videos within the configured recent-video window;
  - retain older videos up to at least `max(5, ceil(ListRenderMaxItems / channelCount * oversamplingFactor))` per channel;
  - keep ordering deterministic.
- Revisit the rule where necessary to provide a hard serialized-size safety ceiling even when recent-video volume or channel count is unusually high.
- Define and document the maximum supported list/channel/video cardinalities and the user-visible behavior when a list would exceed them.
- Ensure add-channel seeding and worker projection refreshes apply the same sizing policy.
- Keep the final rendered global video limit and stale-channel behavior unchanged.
- Update the Cosmos schema and implementation-contract docs with the final invariant and safety ceiling.

## Out Of Scope

- Changing SQL normalization.
- Production infrastructure provisioning.
- SQL-to-Cosmos migration.

## Validation

- Unit tests cover the recent window, per-channel allocation, oversampling, deterministic ties, empty lists, one-channel lists, and high-channel-count lists.
- Tests serialize representative worst-case list documents and prove they remain below the documented UTF-8 safety ceiling.
- Emulator tests exercise add and projection replacement near supported cardinality limits without a 413 response.
- Emulator tests record representative point-read and projection-write RU charges and compare them with documented budgets.
- Build, non-provider tests, LocalDB tests, and opted-in Cosmos tests all pass sequentially with no required test skipped.

## Implementation Summary

Not implemented.
