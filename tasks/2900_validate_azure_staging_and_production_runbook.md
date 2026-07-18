# Task 029: Validate Azure Staging And The Production Runbook

Status: Not Started

Depends On: 2520_establish_cosmos_size_ru_and_resilience_budgets, 2610_harden_cosmos_identity_health_and_observability, 2700_make_worker_safe_for_multi_instance_hosting, 2820_rehearse_migration_reconciliation_and_rollback

## Goal

Demonstrate that the complete application, infrastructure, identity, worker, lifecycle, observability, and migration design operate together under production-like Azure conditions.

## Scope

- Deploy the production build and production-equivalent infrastructure definition to an isolated Azure staging environment.
- Run authenticated anonymous-link HTTP flows for create, view, edit, add/remove channel, share create/consume/delete, force refresh, and delete.
- Run the real hosted worker with at least two application instances and verify lease coordination and crash recovery.
- Exercise expected cardinality, list projection size, TTL cleanup, reconciliation, 429 throttling, Cosmos interruption, application restart, and scale-out/scale-in.
- Run a sustained soak long enough to observe multiple refresh, purge/reconciliation, renewal, TTL, and lease cycles.
- Verify dashboards, alerts, health endpoints, backup/restore procedure, incident diagnostics, and cost/RU expectations.
- Finalize deployment, migration, rollback, incident, restore, and post-deployment verification runbooks.
- Record all evidence and unresolved risks in the task summary.

## Out Of Scope

- Production deployment.
- Accepting a failed release criterion based only on manual judgment; exceptions require explicit user approval and documentation.

## Validation

- All production-like HTTP and worker flows pass against Azure Cosmos using managed identity.
- The soak completes with no lost membership, duplicate external batches, stuck recovery work, oversized documents, secret leakage, or unexplained errors.
- RU, latency, throttling, availability, cleanup latency, and cost stay within documented budgets.
- Synthetic failures trigger the expected health state, metrics, alerts, recovery, and operator runbook.
- Backup restore is tested into an isolated environment and reconciles successfully.
- Two complete migration/cutover/rollback rehearsals remain reproducible from the final runbook.

## Implementation Summary

Not implemented.
