# 0007: Use app-owned declarative build profiles

## Status

Superseded by ADR 0010

## Context

A repository-owned driver makes RWL depend on an intrusive support file. Moving the same driver into application storage removes the repository file but preserves an opaque executable configuration surface that is difficult to validate, migrate, or explain during consent.

## Decision

RWL will store each project's build and launch recipe as a build profile in application-owned settings. Its default mode is declarative. During Add Project, RWL will inspect the approved project, propose a typed recipe from recognizable solution, project, package, and artifact files, and let the user confirm or edit it in the UI. The profile will describe ordered dependency restore, build, artifact-selection, and Rhino launch behavior without requiring a driver file in the repository.

## Consequences

- Repository registration no longer depends on `.rhino-worktree-launcher.json` or `Driver.ps1`.
- Build behavior is visible and editable through one application-owned surface.
- Profile schema changes can be migrated with the rest of RWL's application state.
- Automatic discovery must fail into an editable registration screen rather than treating an unfamiliar project as permanently unsupported.
- An imported-driver mode may extend the same build profile without making a driver the default or a repository requirement.
