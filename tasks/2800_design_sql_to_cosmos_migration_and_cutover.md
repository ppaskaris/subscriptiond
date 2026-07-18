# Task 028: Design SQL-To-Cosmos Migration And Cutover

Status: Not Started

Depends On: 2300_complete_storage_agnostic_repository_boundaries, 2600_add_production_cosmos_infrastructure, 2700_make_worker_safe_for_multi_instance_hosting

## Goal

Define a safe, restartable, verifiable migration and rollback procedure for moving the existing SQL-backed production data to Cosmos without losing anonymous secret links or worker state.

## Scope

- Inventory every SQL source table/field and map it to the final Cosmos document shapes.
- Define handling for list tokens, share links, channel/video projections, reverse references, status, TTL, renewal dates, and worker state.
- Choose offline, dual-write, or bounded-downtime cutover and justify the choice for this service's scale and risk.
- Define deterministic projection construction and absolute expiration-to-TTL calculation at migration time.
- Define checkpointing, idempotency, resume, retry, poison-record handling, rate limiting, and RU consumption.
- Define source-to-target reconciliation at record, membership, projection, and domain-visible levels.
- Define freeze, final delta, configuration switch, smoke check, rollback trigger, rollback mechanics, and post-cutover SQL retention.
- Threat-model migration logs/output so tokens, passwords, and connection strings remain secret.
- Produce an operator runbook and update the relevant architecture documents.

## Out Of Scope

- Implementing the migration command.
- Executing production migration.
- Deleting the SQL database.

## Validation

- The mapping accounts for every persisted SQL field and every required Cosmos field.
- The design proves rerunning from any checkpoint cannot duplicate membership, revive expired data, extend TTL incorrectly, or consume share links.
- Reconciliation and rollback have objective pass/fail thresholds.
- Expected dataset size, RU, runtime, and allowed downtime are estimated from production-like data.
- The design is reviewed against the anonymous secret-link and recovery invariants from earlier tasks.

## Implementation Summary

Not implemented.
