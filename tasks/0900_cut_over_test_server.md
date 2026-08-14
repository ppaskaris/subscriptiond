# Task 0900: Cut Over The Test Server

Status: Not Started

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

Not implemented.
