# Task 020: Bound Cosmos List Projections

Status: Completed

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
- Cloud infrastructure provisioning or deployment automation.
- SQL-to-Cosmos migration.

## Validation

- Unit tests cover the recent window, per-channel allocation, oversampling, deterministic ties, empty lists, one-channel lists, and high-channel-count lists.
- Tests serialize representative worst-case list documents and prove they remain below the documented UTF-8 safety ceiling.
- Emulator tests exercise add and projection replacement near supported cardinality limits without a 413 response.
- Emulator tests record representative point-read and projection-write RU charges and compare them with documented budgets.
- Build, non-provider tests, LocalDB tests, and opted-in Cosmos tests all pass sequentially with no required test skipped.

## Implementation Summary

Implemented one shared Cosmos list-projection sizing policy for add-channel
seeding, membership/settings/renewal replacements, and worker projection
replacements. The policy deterministically orders channels and videos, removes
duplicate video ids, retains the configured five-day recent window, fills older
videos to the oversampled per-channel allocation, and validates the complete
serialized item before a write. Selection now returns a fresh DTO graph on every
list and ETag attempt so fan-out and membership-changing retries cannot underfill
later projections.

Documented and enforced a supported envelope of 100 channels, 100 canonical
videos per channel, 500 projected videos per list, and a strict 1,900,000-byte
UTF-8 ceiling (197,152 bytes below the Cosmos 2-MiB item limit). Unsupported
add-channel requests leave membership unchanged and return a form-level capacity
message; unsupported worker projections retain the last bounded list document.
The final 100-video global render limit and stale-channel behavior are unchanged.
Quota-increasing removals selectively point-read and rehydrate only underfilled
remaining canonical channels, including unavailable channels; already-full
projections incur no extra canonical read. If enrichment would cross the
projected-video or byte ceiling, removal falls back to the existing bounded
embedded projections. A missing canonical channel uses the same embedded
projection, so capacity pressure and 404s cannot prevent a list from shrinking;
reverse-reference cleanup follows a successful membership write or confirmation
that the membership or list is already absent.

Added unit coverage for the recent boundary, oversampled allocations,
deterministic ties and duplicate ids, empty/one-channel/high-channel lists,
unsupported recent volume, both repository write paths, user-visible capacity
errors, multi-list fan-out in both orders, ETag retry after channel-count
reduction, removal rehydration, unavailable remaining channels, and
representative maximum-field UTF-8 size. Removal tests also cover projected-video
and byte-ceiling fallback, canonical 404, and a 412 retry that changes membership
and recomputes the hydration set. The production client, byte preflight, and
emulator fixture now use the same custom Cosmos serializer. Added an
emulator test that adds the 100th channel, replaces a projection at the 500-video
limit with an actual 1,773,836-byte payload, verifies no 413 response, and guards
measured RU budgets. Updated the Cosmos schema and implementation contracts and
incremented `AssemblyVersion` to `2.18.0.0`.

Validation passed sequentially on 2026-07-25:

- solution build: 0 warnings, 0 errors;
- tests excluding LocalDB and Cosmos: 176 passed, 0 failed, 0 skipped;
- opted-in LocalDB tests with `YOUTUBED_RUN_LOCALDB_TESTS=true`: 76 passed,
  0 failed, 0 skipped;
- opted-in Cosmos emulator tests with `YOUTUBED_RUN_COSMOS_TESTS=true`: 28
  passed, 0 failed, 0 skipped;
- near-ceiling maximum-cardinality emulator measurement: 1,773,836 serialized
  bytes, 291.80 RU list point read, and 2,500.77 RU representative projection
  replacement, below the documented 350 RU and 3,000 RU regression budgets;
- `git diff --check`: passed.
