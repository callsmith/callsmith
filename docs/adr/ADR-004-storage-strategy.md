# ADR-004: Storage Strategy — Filesystem vs SQLite

**Date:** 2026-03-13  
**Status:** Accepted

---

## Context

Callsmith needs to persist two fundamentally different categories of data:

1. **User-owned, human-readable content** — request collections, environment
   files. These should be inspectable, editable in any text editor, committable
   to git, and shareable with a team without any export/import ceremony.

2. **Application-managed structured data** — request history. This is append-only,
   potentially large (thousands of entries), and needs powerful filtering and
   search (by status code, body content, URL pattern, date range, etc.).

Storing everything in SQLite would make collections opaque binary blobs that
cannot be diffed or version-controlled. Storing history as flat files would make
filtering slow and fragile.

## Decision

Use **two different storage strategies** based on data category:

### Filesystem (plain files on disk)

Used for: request collections and environment files.

- A **collection** is a folder on disk.
- Each **request** is a single `.callsmith` file (JSON format) in the folder.
- Sub-folders represent sub-collections.
- An `environment/` sub-folder holds environment files (one file per environment).
- The user controls the location — they open any folder as a collection, just
  like a code editor opens a project folder.

Why:
- Human-readable and git-diff-friendly.
- No import/export needed — the filesystem IS the data store.
- Teams can share collections by committing to a repository.
- External edits (from a text editor or `git pull`) are reflected without restart.

### SQLite (via EF Core)

Used for: request history.

Why:
- History is append-only and grows large — flat files would not scale.
- History needs rich, multi-field filtering that is impractical with filesystem
  scans.
- History is application-managed data, not user-owned content. Users don't
  need to inspect or edit it directly.
- SQLite handles this workload well for a single-user desktop application.

## Rule for agents

When deciding where to store new data, apply this test:

> "Would the user want to open this in a text editor or commit it to git?"
>
> - **Yes** → filesystem (plain files).
> - **No** → SQLite.
