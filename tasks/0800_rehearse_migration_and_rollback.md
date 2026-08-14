# Task 0800: Rehearse Migration And Rollback

Status: Not Started

Depends On: 0700_implement_offline_sql_to_cosmos_import

## Goal

Prove the complete stopped-site migration, pre-open rollback, and operator runbook on representative
data before touching the test server.

## Scope

- Build or sanitize a dataset representative of the test server without copying secret values into
  tracked files or evidence.
- Rehearse disabling share creation, waiting out the maximum share-link lifetime, confirming the
  drain, stopping writes, and running `validate`, `import`, and `reconcile`.
- Exercise an interrupted import followed by an idempotent rerun into the same pre-cutover target.
- Start the application on Cosmos with traffic still closed and run the complete smoke checklist.
- Inject a failed reconciliation and a failed smoke test and prove configuration rollback to the
  unchanged SQL source before traffic opens.
- Measure migration duration, downtime, RU, throttling, and smoke duration.
- Produce a concise operator checklist containing exact commands, expected safe outputs, decision
  points, rollback boundary, and evidence locations.

## Out Of Scope

- Production/test-server mutation.
- Rollback after Cosmos has accepted public writes.
- Weakening reconciliation to make a rehearsal pass.

## Validation

- Two clean rehearsals produce identical reconciled target state.
- At least one rehearsal includes interruption/rerun and one includes pre-open rollback.
- Known anonymous list URLs authenticate after import without exposing their tokens in evidence.
- List membership, status, videos, expiry/TTL, renewal, request-driven refresh, and share flows pass.
- Evidence contains no secrets or real personal metadata.
- All application/provider suites and deployment prechecks required by `AGENTS.md` pass immediately
  before the final rehearsal.

## Implementation Summary

Not implemented.
