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
Proof, observed from outside Rhino, that the launched Rhino process holds the canonical plug-in artifact mapped in its address space. It does not claim plug-in authentication or application initialization succeeded, and it makes no claim about critical dependencies, which are existence-checked beside the plug-in during prepare rather than load-gated.
_Avoid_: launch receipt, plug-in readiness

**File-use attribution**:
The external inspection that attributes an open file to the process holding it, which is how RWL observes the launched Rhino process using the canonical plug-in artifact. It requires no code inside Rhino and no integration in the launched plug-in, because a plug-in can never verify its own loading.
_Avoid_: RWL verifier, in-Rhino verification, receipt writer

**Project access grant**:
The user's consent for RWL to inspect one Git repository and every worktree sharing its Git common directory. Build & Launch also authorizes the selected solution to write its ordinary outputs in the selected worktree.
_Avoid_: folder permission, worktree permission

**Remote read grant**:
An independently revocable project permission allowing RWL to read remote Git and pull-request metadata into app-owned storage. It is offered enabled by default alongside project access.
_Avoid_: fetch permission, Git write permission

**Stdio session**:
The pair of standard streams a client owns and an RWL stdio server is reachable through, together with the lifetime they define. The session ends when the client closes those streams or the process bridging them dies; a server that outlives its own session is an orphan, reachable by nobody and serving the release it was spawned from.
_Avoid_: server instance, daemon, background server

**Config**:
The project-specific desktop surface for canonical plug-in project and solution configuration, the desktop launch-mode default, and project grants.
_Avoid_: global settings, MCP setup

**Settings**:
The global desktop surface for MCP client integration.
_Avoid_: project config

**Installable payload**:
The versioned self-contained Windows binaries shared by source-checkout installation and release archives. It is the only distributable product shape.
_Avoid_: developer build, release build
