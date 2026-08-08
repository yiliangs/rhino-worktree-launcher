# Repository isolation

Rhino Worktree Launcher coordinates worktree builds and Rhino launches while keeping launcher-owned state and mutations outside registered repositories.

## Language

**Application-enforced isolation**:
RWL reads only user-approved repository paths and directs its own synchronization, staging, build, launch, and verification writes into RWL-owned storage; it does not claim containment against deliberately malicious build code.
_Avoid_: sandboxed build, read-only application

**RWL workspace**:
An app-owned directory containing a launch source snapshot, dependency installation, intermediate build state, and produced artifacts for one registered project or launch.
_Avoid_: worktree copy, build folder

**Loaded-binary verification**:
Proof produced by RWL inside Rhino that the expected plug-in and declared critical dependencies were loaded from their expected RWL workspace paths; it does not claim plug-in authentication or application initialization succeeded.
_Avoid_: launch receipt, plug-in readiness

**RWL verifier**:
An app-owned Rhino integration that observes loaded plug-in and assembly paths and produces loaded-binary verification without requiring code in the launched plug-in.
_Avoid_: receipt writer, project bootstrap

**Project read grant**:
The user's consent for RWL to read one Git repository and every primary or linked worktree resolved through that repository's Git common directory, independent of worktree names and paths.
_Avoid_: folder permission, worktree permission

**Remote read grant**:
An independently revocable project permission allowing RWL to read remote Git and pull-request metadata into app-owned storage; it is offered enabled by default alongside the required project read grant.
_Avoid_: fetch permission, Git write permission

**Launch source snapshot**:
An app-owned copy of the current user-visible working state, populated from tracked files and untracked nonignored files classified by Git; modified and untracked contents are read from the working tree, and repository metadata, ignored files, caches, and build outputs are excluded unless explicitly included by app-local configuration.
_Avoid_: committed snapshot, repository clone

**Worktree workspace**:
A persistent RWL-owned workspace keyed by Git worktree identity rather than its branch name or filesystem path. Its source snapshot is reconciled exactly before each build while dependencies and incremental build state may be retained until the user clears the cache.
_Avoid_: branch workspace, disposable build copy

**Build profile**:
An app-owned build configuration whose default mode declaratively describes ordered restore, build, artifact-selection, and launch steps. RWL proposes this mode from project discovery during registration and exposes it for editing in the application UI; an advanced mode may instead use a user-selected driver imported into application storage.
_Avoid_: required driver script, repository build configuration

**Imported driver**:
An optional user-supplied build driver copied into RWL-owned storage and executed against an RWL workspace under an explicit input-and-artifact contract. RWL does not depend on the selected source file after import.
_Avoid_: repository driver, linked script
