# Task 0900: Cut Over The Test Server

Status: Completed

Depends On: 0800_rehearse_migration_and_rollback

## Goal

Perform the explicitly authorized, bounded-downtime test-server migration to the validated Cosmos
free-tier deployment and retain a clear rollback source.

## Authorization Gate

This task describes an external production-like mutation. Do not begin it merely because its
dependency is complete. Obtain explicit user authorization for the migration window, Azure changes,
application stop/start, configuration switch, and deployment.

## Scope

- Confirm the reviewed artifact, configuration, empty Cosmos target, SQL backup/retention decision,
  operator checklist, and rollback owner.
- Disable share creation and complete the documented drain.
- Stop public writes, run migration validation/import/reconciliation, switch the provider, and
  deploy the reviewed build.
- Run the pre-open smoke checklist while SQL remains unchanged.
- Roll back to SQL immediately if any pre-open reconciliation or smoke criterion fails.
- Open traffic only after every criterion passes and the user accepts the no-dual-write rollback
  boundary.
- Observe request failures, RU, throttling, refresh queue behavior, TTL, and storage during the
  agreed post-cutover window.
- Retain SQL unchanged for the agreed period and record the eventual disposal decision separately.

## Out Of Scope

- Deleting the SQL database.
- Automatic deployment or unattended cutover.
- Direct rollback to stale SQL after Cosmos has accepted user mutations.
- Expanding beyond one application instance.

## Validation

- Pre-cutover build, all provider suites, format, diff, vulnerability, migration validation, and
  Azure smoke checks are current and passing.
- Import reconciliation meets every rehearsed threshold before configuration switch.
- Known list URLs, renewal, channel management, refresh, add/remove, sharing, and deletion pass
  before traffic opens.
- Post-open Azure metrics stay within documented throughput/storage bounds with no secret leakage
  or unexplained failures.
- The task summary records timestamps, decisions, sanitized evidence, retained SQL location, and the
  exact point after which rollback requires a separate delta plan.

## Implementation Summary

- Cut over the bespoke `youtubed-release-test-app` App Service in the shared `youtubed-gr`
  resource group without deleting or modifying the resource group or production SQL resources. The
  source application was already in a user-managed maintenance window and the test app was stopped.
  The user explicitly authorized the Azure configuration changes, deployment, application
  stop/start, migration, smoke writes, traffic opening, and the no-dual-write rollback boundary.
- Used the production application SQL principal whose default schema is `youtubed`; the initially
  supplied database-administrator principal defaulted to `dbo` and failed read-only validation
  safely with zero target operations. The user confirmed there were no share links and explicitly
  approved skipping the 76-minute drain; the stopped-source SQL query independently returned zero
  valid unconsumed links. The unchanged SQL source is backed up at the user-owned
  `C:\Users\Patrick\Downloads\subscriptiond_backup\database.bacpac`, with Patrick as rollback owner
  and indefinite retention.
- Verified the test deployment shape on 2026-08-15: free tier, one Canada East region, periodic
  backup, 1,000 RU/s manual shared database throughput, the expected `lists`, `channels`, and
  `shareLinks` policies, an F1 single-instance App Service plan, and no autoscale. The test app
  connection secret matched the supplied Cosmos target without exposing it.
- Passed the release gates sequentially before migration: build with zero warnings/errors, 166
  ordinary tests, 51 opted-in LocalDB tests, 28 opted-in Cosmos emulator tests, formatting,
  whitespace, and direct-plus-transitive NuGet vulnerability checks. Deployed a fresh Release build
  of commit `03f550d8a738e2748134e6736d6f8561674bcd7e`, assembly version `2.30.0.0`, using the publish
  profile verified to target only `youtubed-release-test-app`.
- Migration validation reported 5 non-expired lists and 66 referenced channels with reconciliation
  hash `a575897ebb8060ae430d4928821560fd1d2d992fa67709a72ad8d53d20b98372` and zero target
  operations. The verified-empty import completed in 2,955.73 ms with 74 post-initialization target
  operations, 649.86 RU, and no surfaced throttles. Reconciliation completed in 1,437.83 ms with the
  same counts/hash, 3 target operations, 11.56 RU, and no surfaced throttles.
- Kept the application closed with a temporary operator-only App Service access restriction for
  deployment and pre-open smoke. The known imported list authenticated and rendered its expected
  title and channel. Synthetic checks passed create, add/remove, cache-hit re-add, forced refresh,
  video rendering, the disabled-share HTTP 503 guard, share create/list/one-time consume/reuse
  rejection/delete, and list deletion with anonymous-URL invalidation. A token-free projection of
  the imported known list confirmed its title, one-channel membership, same-day renewal, and
  positive TTL. All synthetic lists were removed. Three share documents left orphaned by failed
  smoke-harness attempts were proven to have been created during the cutover, to reference no
  existing list, and were deleted only after
  explicit user approval. The final gate returned exactly 5 lists, 66 channels, zero share links,
  and zero cutover synthetic lists.
- Temporary filesystem logging was disabled after evidence capture. Because information-level HTTP
  diagnostics contained anonymous paths, the raw local scratch archives and the two exact generated
  App Service application/raw-HTTP log files were deleted; only sanitized aggregate evidence was
  retained. The closed-host observation recorded 151 structured Cosmos requests/501.90 RU, a
  13.33-RU maximum request, 327.82 ms maximum Cosmos latency, zero retries/429s/Cosmos 5xxs, one
  completed refresh with queue depth zero, and no application errors. Its two HTTP 503 responses
  were the intentional disabled-share checks.
- Opened traffic with `Decision=OpenCosmos` at `2026-08-15T13:29:47.9088758Z` after the user accepted
  that direct rollback to frozen SQL is no longer lossless. Ten public known-list samples through
  `2026-08-15T13:34:38.4131586Z` all returned HTTP 200 with expected content (71.10 ms average,
  131.52 ms maximum). Azure metrics for the post-open window reported 11 web requests, zero web 5xx,
  zero queued requests, 47 Cosmos requests/59.94 RU, zero 429s, 5% maximum normalized RU, 393,216
  bytes data usage, and 1,024 bytes index usage. Sanitized evidence is retained under the ignored
  `.local/migration/20260815-test-cutover` directory; credentials and anonymous URLs are not in the
  tracked summary.
