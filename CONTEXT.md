# Canonical solution launch

Rhino Worktree Launcher resolves a Git worktree, optionally builds its canonical Visual Studio solution, and verifies the exact plug-in binaries loaded by Rhino.

## Language

**Canonical solution configuration**:
The project-wide selection of a Rhino plug-in project, Visual Studio solution, solution Configuration, and Platform. RWL stores the selection in application settings and revalidates the same relative project and solution in every selected worktree. Repositories with multiple Rhino plug-in projects require an explicit choice in Config; existing `.rhp` outputs are never used as project identity.
_Avoid_: app-owned build profile, project build recipe

**Canonical plug-in artifact**:
The `.rhp` at the Rhino plug-in project's MSBuild `TargetPath` for the mapped project configuration inside the selected worktree. It is the only project artifact RWL registers and asks Rhino to load.
_Avoid_: RWL artifact, copied plug-in

**Launch mode**:
The choice for one launch request between Build & Launch and Direct Launch. Desktop Config stores a project-specific default for mechanical launches; MCP agents choose explicitly per request. Build & Launch builds the canonical solution before resolving its artifact, while Direct Launch loads the existing artifact without building or claiming it is fresh.
_Avoid_: project-wide launch mode, freshness mode, build receipt

**Loaded-binary verification**:
Proof produced inside Rhino that the expected plug-in and declared critical dependencies were loaded from their exact canonical paths. It does not claim plug-in authentication or application initialization succeeded.
_Avoid_: launch receipt, plug-in readiness

**RWL verifier**:
An app-owned Rhino integration that observes loaded plug-in and assembly paths and produces loaded-binary verification without requiring code in the launched plug-in.
_Avoid_: receipt writer, project bootstrap

**Project access grant**:
The user's consent for RWL to inspect one Git repository and every worktree sharing its Git common directory. Build & Launch also authorizes the selected solution to write its ordinary outputs in the selected worktree.
_Avoid_: folder permission, worktree permission

**Remote read grant**:
An independently revocable project permission allowing RWL to read remote Git and pull-request metadata into app-owned storage. It is offered enabled by default alongside project access.
_Avoid_: fetch permission, Git write permission

**Config**:
The project-specific desktop surface for canonical plug-in project and solution configuration, the desktop launch-mode default, and project grants.
_Avoid_: global settings, MCP setup

**Settings**:
The global desktop surface for MCP client integration.
_Avoid_: project config
