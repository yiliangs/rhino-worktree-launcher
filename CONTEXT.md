# Canonical solution launch

Rhino Worktree Launcher resolves a Git worktree, optionally builds its canonical Visual Studio solution, and verifies the exact plug-in binaries loaded by Rhino.

## Language

**Canonical solution configuration**:
The project-wide selection of a Rhino plug-in project, Visual Studio solution, solution Configuration, and Platform. RWL stores the selection in application settings and revalidates the same relative project and solution in every selected worktree. Repositories with multiple Rhino plug-in projects require an explicit choice in Config; existing `.rhp` outputs are never used as project identity.
_Avoid_: app-owned build profile, project build recipe

**Canonical plug-in artifact**:
The `.rhp` at the Rhino plug-in project's MSBuild `TargetPath` for the mapped project configuration inside the selected worktree. It is the only project artifact RWL registers and asks Rhino to load.
_Avoid_: RWL artifact, copied plug-in

**Opening project**:
The registered project the desktop selects when it starts: the one the catalog recorded as last selected, or the first by display name when nothing is recorded or the recorded project is no longer registered. `ProjectCatalogView.SelectedProject` carries it, and it is the catalog's answer rather than a report of what the drop-down currently shows.
_Avoid_: default project, recent project, active project

**Launch mode**:
The choice for one launch request between Build & Launch and Direct Launch. The desktop footer and the MCP tools both name the mode per request; Desktop Config stores a project-specific default, which the row's mode chip reports and which Enter on the worktree list and the CLI pass. Build & Launch builds the canonical solution before resolving its artifact, while Direct Launch loads the existing artifact without building or claiming it is fresh.
_Avoid_: project-wide launch mode, freshness mode, build receipt

**Launch executor**:
The process that carries out one launch: pending-journal recovery, the registration lease across both registry hives, the Rhino start, loaded-binary verification, the restore, and the correction once Rhino exits. The interactive Windows shell starts it, because a launcher host can run with its current-user registry writes intercepted and cannot detect that by reading its own writes. A launcher host is the desktop, CLI, or MCP server that asked for the launch: it resolves the worktree, builds, and names the artifact, and it never mutates a registration.
_Avoid_: launch broker, launch service, background launcher

**Registration visibility check**:
The confirmation by a separately spawned reader that the registration this launch just wrote is present in the registry, distinguished from an identical one left by an earlier launch through a per-launch nonce removed once confirmed. A launch whose registration no independent reader can see ends before Rhino starts.
_Avoid_: seed self-check, registry readback

**Loaded-binary verification**:
Proof, observed from outside Rhino, that the launched Rhino process holds the canonical plug-in artifact mapped in its address space. It does not claim plug-in authentication or application initialization succeeded, and it makes no claim about critical dependencies, which are existence-checked beside the plug-in during prepare rather than load-gated.
_Avoid_: launch receipt, plug-in readiness

**File-use attribution**:
The external inspection that attributes an open file to the process holding it, which is how RWL observes the launched Rhino process using the canonical plug-in artifact. It requires no code inside Rhino and no integration in the launched plug-in, because a plug-in can never verify its own loading.
_Avoid_: RWL verifier, in-Rhino verification, receipt writer

**Rhino instance attribution**:
The point-in-time reading of which live Rhino processes exist and which plug-in artifacts each holds mapped. It is file-use attribution asked without an expected path, so it is plug-in agnostic and observes any Rhino, not only one RWL launched. Concurrent launches legitimately leave several verified Rhino processes running, each a different build, so this is what binds an interaction to the right one when the caller does not already hold a launch result's process id. A Rhino this account cannot open for a virtual-memory read is reported as unattributable with the reason, never omitted.
_Avoid_: Rhino session monitor, instance tracker, process watch

**Launch identity stamp**:
The `RWL_LAUNCH_ID` and `RWL_ARTIFACT` environment variables the launch executor sets on the Rhino it starts, so code running inside Rhino identifies its own launch by reading its own environment. RWL never reads them back from another process; the process id belonging to a launch is already in that launch's result and log.
_Avoid_: launch handshake, in-Rhino receipt, callback token

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
