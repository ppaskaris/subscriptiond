# Task 015: Enable Cosmos Provider And Validate End To End

Status: Not Started

Depends On: 012a_implement_cosmos_list_repository, 012b_implement_cosmos_sharelink_repository, 013a_implement_cosmos_channel_repository, 013b_implement_cosmos_projection_repository, 014_implement_cosmos_worker_state_and_purger

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

Not completed.
