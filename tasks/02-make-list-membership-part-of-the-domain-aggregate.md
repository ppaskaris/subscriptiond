# Task 02: Make list membership part of the domain aggregate

Implement this task after Task 01. Make `SubscriptionList` accurately represent the list aggregate by including its authoritative channel membership. This prepares list reads to stop depending on persistence-specific projection shapes.

## Required changes

- Add a storage-agnostic, read-only collection of channel IDs to `SubscriptionList`, defaulting to an empty collection.
- Update Cosmos list document mapping in both directions so membership is included whenever a `SubscriptionList` is loaded or persisted.
- Preserve the existing membership invariants:
  - IDs are non-null and non-blank.
  - IDs are unique using ordinal comparison.
  - IDs have deterministic ordinal ordering when serialized.
  - A list contains at most 100 channel IDs.
- Update service/model mapping and tests so converting or passing a list does not silently discard membership.
- Prefer existing mapping and normalization utilities; do not introduce a second membership-normalization implementation.

## Constraints

- The list document remains the sole membership authority.
- Do not put Cosmos ETags, partition keys, TTL values, or document types on the domain model.
- Do not embed channel metadata or videos in `SubscriptionList`.
- Do not change the stored Cosmos document schema beyond representing the already-existing `channelIds` field through the domain model.
- Do not change list capacity, URL behavior, authentication, or refresh behavior.

## Acceptance criteria

- A list loaded through `IListRepository.GetAsync` contains its channel IDs.
- Creating and updating lists preserves normalized membership without losing IDs.
- Domain and repository interfaces remain free of Cosmos SDK and Cosmos document types.
- Mapping tests cover empty membership, representative membership, duplicate/order normalization, and the supported cardinality limit.
- Existing persisted documents with a missing or null `channelIds` value, if currently supported, continue to load as an empty membership collection.

## Validation

Because this changes Cosmos document mapping, run validation sequentially: build the solution, run tests excluding Cosmos, then run the opted-in Cosmos emulator suite with `YOUTUBED_RUN_COSMOS_TESTS=true`. Include the existing document-size and representative request/RU assertions applicable to list documents. Report unavailable or skipped checks as unverified.
