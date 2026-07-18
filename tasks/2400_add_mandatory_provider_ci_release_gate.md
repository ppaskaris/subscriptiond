# Task 024: Add A Mandatory Provider CI Release Gate

Status: Not Started

Depends On: 2300_complete_storage_agnostic_repository_boundaries

## Goal

Make every change prove SQL and Cosmos behavior in a repeatable repository-owned continuous-integration pipeline where required tests cannot silently skip.

## Scope

- Add a tracked CI workflow that runs on pull requests and the protected production branch.
- Build once, then run non-provider tests, LocalDB tests, and Cosmos emulator tests sequentially.
- Provision or connect to reliable isolated LocalDB and Cosmos emulator instances in CI.
- Publish TRX logs, coverage, and useful emulator/service diagnostics on failure.
- Fail when required LocalDB or Cosmos tests are skipped, not merely when tests fail.
- Add a coverage report and set an initial meaningful threshold for production and provider code; document how the threshold can only move upward or be intentionally reviewed.
- Add format verification, `git diff --check`, and direct-plus-transitive NuGet vulnerability scanning.
- Document the same release-validation command sequence for local execution.

## Out Of Scope

- Azure production deployment.
- Replacing xUnit.
- Treating emulator validation as a substitute for later Azure staging validation.

## Validation

- A clean CI run executes every required suite with zero skips and publishes its artifacts.
- A controlled test demonstrates that disabling/unavailable LocalDB or Cosmos fails the workflow.
- A controlled formatting or vulnerable-package policy failure is detected by the workflow.
- The workflow does not expose connection strings, emulator keys, list tokens, or other secrets.
- Local documentation reproduces the CI command order successfully.

## Implementation Summary

Not implemented.
