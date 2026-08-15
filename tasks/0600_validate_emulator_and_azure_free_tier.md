# Task 0600: Validate Emulator And Azure Free Tier

Status: Completed

Depends On: 0500_implement_share_links_and_enable_cosmos

## Goal

Establish a small evidence-based release envelope and prove the simplified provider against an
actual correctly provisioned Azure Cosmos DB free-tier database.

## Scope

- Define small, representative, and maximum supported list/channel datasets without padded
  synthetic metadata unrelated to the application's real limits.
- Measure serialized size, request count, RU, and latency for list rendering/renewal, add/remove,
  channel refresh, and share operations in the emulator.
- Retain a clear item-size safety margin and confirm the shared workload leaves the documented 30%
  throughput reserve under representative traffic.
- Provision or identify one manually approved free-tier test account and database following
  [`../docs/migration-and-operations.md`](../docs/migration-and-operations.md).
- Verify free-tier eligibility, 1,000 RU/s manual shared database throughput, one region, and exactly
  the three intended shared-throughput containers.
- Run a secret-safe Azure smoke test for every application flow and compare request shape and RU to
  emulator observations without requiring exact equality.
- Verify startup detects a deliberately wrong safe-to-test container policy in an isolated test
  database.
- Record the single-instance deployment constraint and ensure App Service scale-out is disabled.

## Out Of Scope

- Production data migration or cutover.
- Hosted CI, automatic deployment, dashboards, or an enterprise alert suite.
- Raising limits merely to make a measurement pass.

## Validation

- Emulator size/RU regression tests pass for all three representative shapes.
- The Azure smoke test passes against shared free-tier throughput without unhandled throttling.
- Azure portal/SDK evidence confirms the intended account, database, throughput, region, container,
  TTL, partition-key, and indexing configuration without exposing credentials.
- Logs contain no tokens, share passwords, keys, connection strings, bodies, or raw diagnostics.
- Build, non-provider tests, LocalDB tests, Cosmos emulator tests, format verification,
  `git diff --check`, and the direct-plus-transitive vulnerability scan pass sequentially.

## Implementation Summary

- Added a repeatable emulator release-envelope integration test for small (1 channel/3 videos),
  representative (10/20), and maximum (100/100) datasets using ordinary application metadata.
  It measures serialized list/channel sizes plus request count, RU, latency, and request shape for
  same-day and renewal rendering, full cache-miss add, cache-hit add, remove, channel refresh, and
  share create/list/consume/delete.
  It enforces a 512-KiB item ceiling, a 700-RU per-operation ceiling, and a representative traffic
  envelope that preserves the documented 30% reserve on 1,000 RU/s. The shared structured-request
  recorder also asserts that evidence contains no token, share password, connection string, body,
  or raw-diagnostics content. A focused emulator test injects two 429 responses through the real
  Cosmos SDK HTTP transport, configures one SDK retry, proves exhaustion surfaces the second 429,
  and verifies that the final application telemetry remains sanitized.
- Added the secret-safe release-validation runbook and a read-only Azure CLI verifier for free-tier
  enablement, one region, 1,000 RU/s manual shared database throughput, exactly three containers,
  partition key, TTL, all supported indexing-policy facets (including vector/full-text policy), and
  a one-instance App Service plan without enabled autoscale. Its sanitized evidence includes a
  per-container TTL/indexing summary. The runbook explicitly separates these safe reads from Azure
  smoke-test and deliberate policy-drift mutations, which require approval and an isolated test
  database; the local evidence destination has a repository-scoped ignore rule, and the verifier
  resolves the evidence filename and creates only its missing parent directory before writing.
- Emulator evidence on 2026-08-14: the maximum list was 2,981 serialized bytes and a maximum
  channel was 19,244 bytes. A maximum same-day render used two requests and 36.40 RU. The defined
  representative traffic workload, including one cache-miss and one cache-hit add per minute,
  measured 10.35 RU/s against the 700 RU/s ceiling.
- Local validation passed sequentially: build (zero warnings/errors), 151 non-provider tests, 50
  opted-in LocalDB tests, and 27 opted-in Cosmos emulator tests; format verification, `git diff
  --check`, PowerShell syntax parsing and a no-Azure nested evidence-directory test for the Azure
  verifier, and the direct-plus-transitive NuGet vulnerability scan also passed. `AssemblyVersion`
  was incremented from `2.27.0.0` to `2.28.0.0`.
- Azure evidence on 2026-08-14: the approved free-tier shape passed before testing and after
  restoration: one region, 1,000 RU/s manual shared database throughput, exactly the three intended
  container policies, one App Service worker, and no enabled autoscale. Hosted create/render/daily
  renewal, channel cache-miss/add/refresh/remove/cache-hit, share create/list/consume/delete, and
  list-delete flows passed. The combined captured flows produced 74 sanitized structured request
  events totaling 218.82 RU; the maximum request was 13.33 RU, maximum observed latency was
  249.87 ms, retry count was zero, and only expected not-found reads occurred. Request shapes
  agreed with the emulator evidence without requiring exact RU or latency equality, no throttling
  was left unhandled, and evidence scans found no secrets, bodies, or raw diagnostics.
- Because the free-tier account's total throughput was capped at 1,000 RU/s, the user explicitly
  approved the documented zero-cost drift-test fallback. With the application stopped, only the
  verified-empty `lists` container was replaced by a wrong-partition-key container. Production
  startup failed safely with HTTP 500 and a sanitized policy message. The replacement was freshly
  verified empty before deletion, the exact original container policy was restored, and the full
  verifier plus hosted HTTP health check passed afterward. The verifier's Windows PowerShell
  compatibility follow-up increments `AssemblyVersion` from
  `2.28.0.0` to `2.28.0.1`. Final local validation passed sequentially: build with zero warnings
  and errors, 151 non-provider tests, 50 opted-in LocalDB tests, 27 opted-in Cosmos emulator tests,
  format verification, Windows PowerShell 5.1 syntax and multi-line/single-item/empty-array JSON
  compatibility checks, `git diff --check`, and the direct-plus-transitive vulnerability scan.
