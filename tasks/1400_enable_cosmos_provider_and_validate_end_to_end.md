# Task 015: Enable Cosmos Provider And Validate End To End

Status: Completed

Depends On: 1100_implement_cosmos_list_repository, 1110_implement_cosmos_sharelink_repository, 1200_implement_cosmos_channel_repository, 1210_implement_cosmos_projection_repository, 1300_implement_cosmos_worker_state_and_purger

## Goal

Wire the Cosmos provider into application configuration and validate the application flow end to end against the Cosmos emulator.

## Scope

- Complete Cosmos provider DI registration.
- Run provider contract tests against Cosmos.
- Exercise create list, add channel, worker refresh, share link, and delete flows against Cosmos emulator.
- Measure or log rough RU usage for key operations if the emulator/SDK exposes it.

## Out Of Scope

- Production Azure deployment.
- SQL-to-Cosmos migration tooling.

## Validation

- All unit tests pass.
- LocalDB tests pass.
- Cosmos emulator tests pass when opted in.
- Manual end-to-end smoke test with Cosmos provider.

## Implementation Summary

Enabled `Persistence:Provider=Cosmos` by registering a shared configured Cosmos
client, database/container context, all provider-neutral Cosmos repositories, the
TTL-backed expiration purger, and the worker state store. A startup hosted service
now creates the configured database and narrowed-index containers before the
unified worker starts. Configuration accepts either a connection string or endpoint
and key and fails early with an actionable message when credentials are absent.

Added debug-level Cosmos SDK request-charge logging and an emulator-backed
end-to-end test that uses the configured DI provider to create a list, discover and
add a channel, run the worker refresh pipeline, render its projected video, create
and consume/delete a share link, delete the list, and verify reverse-reference
cleanup. The smoke test also measures and reports the list point-read RU charge.

Validation passed on 2026-07-18:

- Solution build: 0 warnings, 0 errors.
- Unit tests excluding LocalDB and Cosmos: 155 passed.
- LocalDB integration tests with `YOUTUBED_RUN_LOCALDB_TESTS=true`: 76 passed.
- Cosmos emulator tests with `YOUTUBED_RUN_COSMOS_TESTS=true`: 27 passed,
  including all provider contracts and the configured-provider end-to-end smoke.

Corrective follow-up on 2026-07-18: removed the obsolete application-level
`ChannelVideoService` registration, whose SQL-only repository dependency prevented
Development DI validation when Cosmos was selected. Added an emulator-backed full
`Program` host startup test using the Cosmos configuration. The build, 155 unit
tests, 76 LocalDB tests, and all 27 Cosmos tests passed after the correction.
