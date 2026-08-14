# Task 0300: Add The Three-Container Cosmos Foundation

Status: Not Started

Depends On: 0200_simplify_refresh_and_maintenance

## Goal

Introduce the new Cosmos client, document types, container policies, and emulator harness without
yet enabling Cosmos as an application persistence provider.

## Scope

- Add the supported Cosmos SDK package and a minimal emulator fixture.
- Add internal document types for lists, channels/videos, and share links exactly as defined in
  [`../docs/cosmos-data-model.md`](../docs/cosmos-data-model.md).
- Add mappings between those documents and storage-agnostic domain objects.
- Configure one client and a serializer shared by production writes and serialized-size tests.
- Define only `lists`, `channels`, and `shareLinks` container names and handles.
- Create narrow indexing and TTL policies: list and share-link TTL enabled, channel TTL disabled,
  secret/video payloads excluded, and no composite recovery indexes.
- Allow development/emulator setup to create its isolated database and containers.
- Make production setup require an existing database with 1,000 RU/s manual shared throughput and
  validate database throughput plus container partition keys, TTL, and indexing before serving.
- Keep `Persistence:Provider=Cosmos` disabled until Task 0500.

## Out Of Scope

- Repository implementations.
- A system or recovery container.
- Dedicated container throughput or serverless provisioning.
- Azure account creation.
- Managed identity.

## Validation

- Unit tests cover document mapping, deterministic membership/video ordering, TTL calculation,
  token secrecy, and representative serialized sizes.
- Emulator tests create exactly three containers and verify partition keys, TTL, and exclusions.
- Tests prove drift detection for wrong partition key, TTL, or indexing policy.
- Configuration tests prove production does not silently create an unprovisioned or dedicated-
  throughput billing shape.
- Build, non-provider tests, LocalDB tests, Cosmos emulator tests, and `git diff --check` pass
  sequentially.

## Implementation Summary

Not implemented.
