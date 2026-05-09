# Task 002: Create Domain And Time Abstractions

Status: Not Started

Depends On: 001_document_target_architecture

## Goal

Introduce storage-agnostic domain objects and `IAppClock` so services can stop depending on Dapper-shaped models and ambient system time.

## Scope

- Add `Domain` types for list, list channel projection, list video projection, channel, channel video, share link, worker state, channel status, and status reason.
- Add `IAppClock` and production implementation.
- Register `IAppClock` in dependency injection.
- Start using UTC timestamps in new code.
- Add unit tests for deterministic time behavior where services are touched.

## Out Of Scope

- Cosmos implementation.
- Worker rewrite.
- SQL schema changes beyond what is needed to compile.

## Validation

- Run unit tests.
- Run LocalDB tests only if touched SQL behavior requires it.

## Implementation Summary

Not completed.
