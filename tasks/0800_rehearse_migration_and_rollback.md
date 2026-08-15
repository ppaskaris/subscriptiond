# Task 0800: Rehearse Migration And Rollback

Status: Completed

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

- Added a default-on `ShareLinks:CreationEnabled` operational switch. Setting it to `false` hides
  the authenticated create control and makes the unchanged create route return HTTP 503 without
  writing, while existing share-link listing, consumption, and deletion remain available for the
  drain. The documented maximum drain wait is 76 minutes, exceeding the configured 75-minute
  maximum lifetime.
- Added secret-safe migration telemetry to every CLI mode: success, total and initialization
  duration, post-initialization target SDK operation count/RU charge, and surfaced 429 count. The CLI
  explicitly reports that initialization is excluded from target-operation metrics; Azure Metrics
  remains the authority for the whole interval and SDK-handled throttles. Existing count/hash output
  and redaction behavior are preserved.
- Expanded the LocalDB-to-emulator import test into a representative two-target rehearsal. It runs
  validation, three interruption points with idempotent recovery, two successful reconciliations
  with identical hashes, imported-token authentication, membership/status/newest-100 video and TTL
  checks, renewal, request-driven refresh queuing, and share create/consume/reuse rejection/delete.
  It injects both a correct-token list-page smoke failure and target list mismatch, requiring the
  latter to fail reconciliation. It starts a production-mode Cosmos-configured application host with traffic
  closed, completes background refresh plus the full HTTP smoke checklist, injects a real list-page
  smoke failure, stops the Cosmos host, starts a separately SQL-configured host, and authenticates
  the unchanged source URL. The reconciliation-mismatch branch starts a distinct SQL-configured
  application host and authenticates the same URL before the smoke-failure branch runs. Sanitized
  output has separate stopped-site, two-rehearsal, smoke, and
  two failure-injection records with downtime, operation/RU, throttles, hashes, and provider rollback
  without tokens, passwords, connection strings, document bodies, or real metadata.
- Added the operator runbook and evidence schema with exact precheck/import commands, safe expected
  outputs, evidence locations, share-drain query, interruption/rerun procedure, private smoke list,
  failure decisions, SQL rollback steps, and the no-dual-write rollback boundary. `.local/migration`
  is ignored. No test-server, Azure configuration, deployment, cutover, or public traffic mutation
  was performed; those remain behind task 0900's explicit authorization gate.
- Final validation passed sequentially on 2026-08-14: build with zero warnings/errors, 166
  non-LocalDB/non-Cosmos tests, 51 opted-in LocalDB tests, and 28 opted-in Cosmos emulator tests.
  `dotnet format --verify-no-changes`, `git diff --check`, and the direct-plus-transitive NuGet
  vulnerability scan passed. The final focused rehearsal proved the 76-minute drain, blocked share
  creation, zero valid links, stopped writes, unchanged SQL source, and 3,198.64 ms downtime.
  Rehearsal 1 passed three interrupted reruns in 564.28 ms with 42 post-initialization target SDK
  operations/257.46 RU; rehearsal 2 passed in 63.36 ms with 9 operations/47.28 RU. Their hashes were
  identical and neither surfaced a throttle. The production-mode Cosmos host completed refresh,
  add/remove/cache-hit re-add, force refresh, share, and delete flows in 2,516.93 ms using 70 logged
  requests/251.32 RU with zero throttles. The injected smoke failure rolled back through a separately
  SQL-configured host in 2,090 ms. The injected reconciliation mismatch independently started a
  distinct SQL-configured host and authenticated the source URL in 35.71 ms.
  `AssemblyVersion` was incremented from `2.29.0.0` to `2.30.0.0`.
