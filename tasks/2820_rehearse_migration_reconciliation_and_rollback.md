# Task 028b: Rehearse Migration, Reconciliation, And Rollback

Status: Not Started

Depends On: 2510_prove_cosmos_ttl_lifecycle, 2810_implement_sql_to_cosmos_migration

## Goal

Prove locally on representative data that migration, cutover, objective reconciliation, restart, and rollback work as documented before touching the test server.

## Scope

- Build or sanitize a test-server-representative SQL dataset without leaking real secret links.
- Run full migration, intentional interruption/resume, final reconciliation, configuration cutover, application smoke tests, and rollback locally.
- Reconcile counts and identifiers for lists, channels, videos, memberships, share links, and worker state.
- Reconcile sampled and boundary domain-visible projections, authentication, renewal, share-link consumption, stale scheduling, and TTL.
- Measure migration duration, RU, throttling, error rate, downtime, and rollback duration.
- Exercise documented rollback triggers, including failed reconciliation and failed post-cutover smoke.
- Update the runbook with measured timings, operator commands, evidence locations, and decision owners.

## Out Of Scope

- Migrating the test server.
- Lowering reconciliation thresholds to make a failed rehearsal pass.
- Destroying the SQL rollback source.

## Validation

- Two consecutive clean local rehearsals meet all reconciliation thresholds.
- At least one rehearsal includes interruption/resume and one includes rollback after an injected post-cutover failure.
- Anonymous list URLs and valid share links work after cutover; consumed/expired links remain unusable.
- Worker scheduling resumes without duplicate external work.
- Evidence contains no tokens, share passwords, connection strings, or personal data.
- Measured downtime and RU stay within the approved design budget.

## Implementation Summary

Not implemented.
