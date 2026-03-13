# ADR-002: SQLite via EF Core for local persistence

**Date:** 2026-03-13  
**Status:** Accepted

---

## Context

Callsmith is a local-first, offline-capable application. It needs to persist
structured data (request history, environment variables, recently opened
collections) without requiring any server process or user-managed database
installation.

Candidates evaluated:

- **PostgreSQL / SQL Server / MySQL** — require a running server process. Not
  suitable for a local-first desktop app.
- **LiteDB** — document database for .NET, but limited tooling and a smaller
  ecosystem than EF Core.
- **Dapper + SQLite** — lightweight, but Dapper is a micro-ORM; migrations,
  change tracking, and schema evolution require manual SQL. Adds friction for a
  fast-moving project.
- **EF Core + SQLite** — full ORM with code-first migrations, LINQ queries,
  change tracking, and strong tooling. SQLite is a single-file, serverless,
  embedded database. Well-supported by EF Core.

## Decision

Use **EF Core** with the **SQLite** provider for all structured local data storage.

Database file locations at runtime:

| OS      | Path                                              |
|---------|---------------------------------------------------|
| Windows | `%APPDATA%\Callsmith\data.db`                     |
| macOS   | `~/Library/Application Support/Callsmith/data.db` |
| Linux   | `~/.config/Callsmith/data.db`                     |

## Consequences

- No server process required — the database is a single file the user can back
  up, inspect, or delete.
- EF Core migrations handle schema evolution cleanly.
- The `Callsmith.Data` project owns all EF Core types; `Callsmith.Core` has zero
  dependency on EF Core.
- SQLite is not suitable for very high-concurrency write workloads, but that is
  not a concern for a single-user desktop tool.

## Scope note

Not all data is stored in SQLite. See ADR-004 for the storage strategy that
differentiates between SQLite and filesystem storage.
