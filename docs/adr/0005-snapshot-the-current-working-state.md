# 0005: Snapshot the current working state

## Status

Superseded by ADR 0010

## Context

Launching only the current commit would omit tracked modifications and untracked source files that Visual Studio and other development tools see in the working tree. Untracked contents do not exist in Git metadata, while tracked modifications must be read from the working-tree files rather than from committed blobs.

## Decision

RWL will use Git as the authority for classifying tracked files and untracked nonignored files, then copy their current working-tree contents into an app-owned launch source snapshot. Git operations used for classification must not mutate repository state. The default snapshot excludes repository metadata and ignored files, including secrets, caches, dependencies, and build outputs. A project may explicitly include a required ignored input through app-local configuration.

## Consequences

- A launch reflects the same saved working-tree state that development tools report, including tracked modifications and untracked nonignored files.
- Unsaved editor buffers remain outside RWL's visibility until the editor saves them to disk.
- Builds operate only on the app-owned snapshot and cannot create ordinary build outputs in the registered repository.
- Projects that intentionally depend on ignored inputs require an explicit app-local include rule.
