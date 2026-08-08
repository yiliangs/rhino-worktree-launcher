# 0006: Retain workspaces by Git identity

## Status

Accepted

## Context

Creating a disposable workspace for every launch would repeatedly restore dependencies and rebuild unchanged outputs. Keying persistent state by a worktree's branch name or current directory would lose the cache when the user renames, moves, or repoints the worktree.

## Decision

RWL will retain one app-owned workspace per stable Git worktree identity. Before each build, it will reconcile the launch source snapshot with Git's current tracked and untracked-nonignored path set, including removing snapshot files that are no longer present in that set. Dependency installations and incremental build state may persist outside the reconciled source area. The UI will provide a clear-cache operation.

## Consequences

- Moving or renaming a worktree does not by itself create a second workspace.
- Repeated launches can reuse dependencies and incremental build state.
- Source reconciliation must be exact; copying only additions and modifications would allow deleted source files to affect later builds.
- Users can discard retained state without touching the registered repository.
