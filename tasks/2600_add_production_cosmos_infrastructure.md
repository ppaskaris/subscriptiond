# Task 026: Add Production Cosmos Infrastructure

Status: Not Started

Depends On: 2520_establish_cosmos_size_ru_and_resilience_budgets

## Goal

Provision repeatable, reviewable Azure Cosmos infrastructure that satisfies the documented free-tier, availability, backup, security, and data-shape requirements.

## Scope

- Add repository-owned infrastructure as code for the Cosmos account, database, four containers, TTL, partition keys, indexing policies, and required composite indexes.
- Provision database-shared or otherwise explicitly budgeted throughput consistent with Task 2520 and the Azure free-tier target.
- Configure free-tier eligibility, regions, consistency, backup/restore policy, networking, diagnostic settings, tags, and deletion protection as appropriate for this service.
- Separate development/emulator, staging, and production parameterization without duplicating resource definitions.
- Decide whether application startup creates resources in development only and validates immutable production resources; implement drift detection with actionable failures.
- Add deployment what-if/plan and idempotency checks.
- Document resource ownership, safe upgrades, restore procedure, and cost expectations.

## Out Of Scope

- Application identity/RBAC wiring, completed in Task 2610.
- SQL-to-Cosmos data migration.
- Deploying the production application before the final release task.

## Validation

- Infrastructure lint/validation and a deployment what-if/plan pass.
- The same definition deploys an isolated staging environment twice without destructive drift or duplicate resources.
- Staging inspection proves throughput, TTL, partition keys, indexes, backup, consistency, diagnostics, and networking match the design.
- A cost calculation demonstrates the expected workload remains within the intended free-tier allowance or explicitly documents approved cost.
- Application startup detects an intentionally mismatched container policy with an actionable error.

## Implementation Summary

Not implemented.
