# Backend and Agent Integration Plan

Status: launcher implementation is under final review for issue #2. Automated backend, CLI, MCP, catalog, routing, receipt, distribution, and live Rhino gates are in place. The August 7, 2026 audit corrected agent-hosted Rhino startup with an interactive-shell bootstrap broker, corrected JSONL diagnostics, and reconfirmed the serialized registry-lease transport described below. Natalie adoption exists on `codex/issue-2-rwl-v2-natalie` but remains unmerged, so the machine's primary checkout and older Natalie worktrees do not yet expose the contract.

## Decision summary

Rhino Worktree Launcher is an independent developer tool for Rhino plug-in repositories. Its responsibilities are project registration, Git worktree discovery, launch orchestration, and loaded-binary verification.

The launcher is a one-off bootstrapper, not a session monitor. A launch runs preflight, build, Rhino start, and loaded-binary verification, reports one terminal result, and exits its role. The launcher does not track running Rhino sessions, does not re-attach to launches it did not start, and holds no durable per-launch state beyond diagnostics logs. Session monitoring is an explicit non-goal for this version.

The system will expose one backend through three adapters:

- The WPF desktop interface for humans.
- MCP tools for agents.
- A CLI for scripting, diagnostics, installation, and fallback use.

Compatible plug-in repositories are registered in the app-local catalog. Project-specific executable integration lives in an app-local project driver; only the in-plug-in receipt writer remains repository code. Repositories need no RWL configuration, driver, or project-specific agent instructions. A machine-level session hook tells an agent when its current checkout belongs to a registered project and when to use the MCP tools.

Rhino's `RHINO_PACKAGE_DIRS` environment variable is a candidate process-level plug-in discovery mechanism within the launch implementation. It is not the product architecture or the problem solver. The README should credit Dale Fugier's published Rhino development configuration while presenting Rhino Worktree Launcher as an independent orchestration tool.

## Product boundary

Rhino Worktree Launcher owns:

- Registering trusted Rhino plug-in repositories on the local machine.
- Resolving the registered project and linked worktree from any path inside it.
- Discovering worktrees and reporting local and remote status.
- Invoking the app-local build and artifact-discovery contract against the selected worktree.
- Starting Rhino with the selected worktree's artifacts.
- Verifying the loaded plug-in and critical dependency paths through the receipt handshake, then reporting one terminal launch result.
- Writing per-launch diagnostics logs.
- Serving the same application commands to the WPF UI, MCP, and CLI.

Rhino Worktree Launcher does not own:

- Tracking, observing, or stopping Rhino sessions after the launch result is reported.
- Product-specific correctness criteria.
- Solver, geometry, or domain validation.
- Repository-specific test selection beyond capabilities explicitly exposed by the project driver.
- General Git worktree creation or source-control policy.
- Project-specific build logic embedded in the generic launcher.
- A requirement that repositories use Claude Code or any other agent host.

## System model

```text
RWL application data
  app-local project driver
              |
              v
Rhino plug-in repository
  in-plug-in receipt writer
              |
              v
Rhino Worktree Launcher backend
  app-local project settings and catalog
  worktree resolver
  application commands
  Rhino process launch and receipt verification
       |              |              |
       v              v              v
   WPF adapter     MCP adapter     CLI adapter
     human            agent         script/fallback
```

The adapters contain transport and presentation logic only. They must not implement independent worktree scanning, readiness, launch, or verification behavior.

## Project contract

RWL stores the project contract in its app-local schema-v4 catalog:

```json
{
  "schemaVersion": 4,
  "projects": [{
    "projectId": "example-plugin",
    "displayName": "Example Plugin",
    "gitCommonDirectory": "C:\\source\\example-plugin\\.git",
    "primaryCheckout": "C:\\source\\example-plugin",
    "driver": {
      "protocolVersion": 1,
      "entrypoint": "projects/example-plugin/Driver.ps1"
    },
    "launch": {
      "rhinoVersion": 8,
      "mode": "rhino-package-directory"
    }
  }]
}
```

Repository manifests are not part of the current architecture. Existing schema-v2 and schema-v3 catalog registrations are imported once under the catalog lock and atomically rewritten as schema v4. A legacy worktree driver may be copied once into application data as migration input; runtime resolution never reads repository configuration or drivers.

The app-local project driver is responsible for project-specific work such as:

- Preflight checks.
- Building the selected worktree.
- Returning the plug-in output directory and expected `.rhp` path.
- Identifying critical managed dependencies that must load from that output.
- Declaring the receipt handshake surface: the request environment variables to set on the Rhino process and the receipt path to await.
- Exposing optional project capabilities without teaching the generic launcher project-specific behavior.

The driver communicates through versioned JSON requests, events, and terminal results. It must support noninteractive execution and deterministic exit behavior. RWL invokes one project driver and supplies the selected checkout as `worktreePath`; linked worktrees do not duplicate launch infrastructure.

### Receipt handshake

Loaded-binary verification requires code running inside the Rhino process. Each adopting plug-in compiles in a small repository-owned bootstrap that watches for the launcher's request environment variable and, when the plug-in finishes loading, writes a receipt recording the launch ID, process ID, executing `.rhp` path, and the loaded paths of declared critical dependencies. The backend sets the request environment on the child Rhino process, waits for the receipt within the launch timeout, and compares actual paths against the driver's expected paths. A launch fails closed when the receipt is missing, mismatched, or reports binaries loaded from outside the selected worktree's output.

Natalie's `WorktreeLaunchBootstrap` is the reference implementation. The launcher repository ships a copyable, dependency-free bootstrap template and documents the receipt JSON schema as part of the driver protocol, so adopters copy one file rather than reverse-engineering an existing project.

## Local project registration

Registration is explicit and occurs once per repository and machine:

```powershell
rwl project register C:\path\to\repository
```

The WPF interface provides the equivalent Add Project action.

Add Project derives default identity and launch settings from the selected Git repository and saves them only in RWL's application directory. It creates an app-local driver automatically, importing a legacy driver from any existing worktree when available or installing a starter otherwise. It never writes or overwrites repository files. The starter fails with an actionable configuration message until its project build and artifact paths are configured.

The local catalog stores stable repository identity and the project contract together:

```json
{
  "projectId": "example-plugin",
  "displayName": "Example Plugin",
  "gitCommonDirectory": "C:\\path\\to\\repository\\.git",
  "primaryCheckout": "C:\\path\\to\\repository",
  "driver": {
    "protocolVersion": 1,
    "entrypoint": "projects/example-plugin/Driver.ps1"
  },
  "launch": {
    "rhinoVersion": 8,
    "mode": "rhino-package-directory"
  }
}
```

Registration validates the app-local contract and provisions the app-local driver. The driver may execute build commands against the selected worktree, so registration remains the machine trust decision. New linked worktrees require no additional registration, configuration, or driver files.

### Catalog access rules

The catalog is read by the WPF application, the CLI, and the MCP server as independent processes, so:

- Ordinary reads never write. The sole exception is one-time schema-v2 migration, which runs under the same lock and atomically replaces the old catalog. An invalid registration surfaces as degraded rather than being pruned.
- Removal happens only through the explicit `RemoveProject` command.
- Writes use atomic temp-file replacement and re-read the current file under a short retry before modifying, so concurrent register and remove operations cannot clobber each other.

No cross-process coordinator is required; the catalog is the only shared mutable state.

## Backend application commands

The backend will expose transport-neutral commands:

- `RegisterProject`
- `RemoveProject`
- `ResolveContext`
- `GetProjectSnapshot`
- `GetWorktreeSnapshot`
- `InspectWorktree`
- `Launch`
- `RunDoctor`

`Launch` is synchronous: it runs preflight, driver build, Rhino start, and receipt verification, then returns one terminal result. It accepts an explicit timeout and always terminates with a machine-readable success or failure. Progress is reported through in-process events for the WPF UI and streamed output for the CLI; there is no separately addressable operation object.

Every command returns typed data and structured diagnostics. Optional enrichment failures, such as unavailable GitHub PR data, are represented as degraded results rather than being silently discarded. Required launch failures remain terminal failures.

Each launch writes a diagnostics log under `%LOCALAPPDATA%\RhinoWorktreeLauncher\logs\` for postmortem reading. The log is inert output, not observable state; nothing reads it programmatically.

## Human WPF interface

The native WPF frontend consumes backend DTOs and application commands. After backend extraction, it must not run Git, invoke project drivers, or infer launch success independently.

A launch interaction follows:

```text
Launch request
  -> progress events
  -> succeeded or failed terminal result
```

The WPF application calls the backend in process. No persistent service or cross-process coordinator exists in this version.

## Agent MCP adapter

A local stdio MCP server will expose a small tool surface over the same backend commands:

- `rhino_worktree_resolve_context`
- `rhino_worktree_list_worktrees`
- `rhino_worktree_inspect`
- `rhino_worktree_launch`
- `rhino_worktree_doctor`

The MCP server contains no launch or project logic. It maps tool requests to backend commands and maps backend results to structured MCP responses.

`rhino_worktree_launch` is one blocking tool call covering build, launch, and verification. Agents wait on long tool calls routinely; the call takes a timeout parameter and always terminates with a structured result. Build and launch are deliberately not split into separate tools, because a split would reintroduce shared state between calls.

The installer registers the MCP server once at user scope. It is then available across all projects on that machine.

## Automatic agent routing

A machine-level `SessionStart` hook calls:

```powershell
rwl context --cwd <session-directory> --json
```

If the directory is not inside a registered project, the hook emits no additional context.

If the directory belongs to a registered project, the hook tells the agent:

- The resolved project and worktree.
- That Rhino launch and loaded-binary verification must use the RWL MCP tools.
- That direct `Rhino.exe` launches and manual plug-in registration changes are not valid fallbacks.
- That ordinary editing, Git operations, and repository-owned headless verification remain outside RWL unless the project contract says otherwise.

The injected context is a hint, not authority. Backend tools resolve the supplied current directory on every call, so an agent that moves between worktrees mid-session still gets correct resolution without any refresh mechanism.

No RWL-specific project-wide `AGENTS.md` or `CLAUDE.md` instructions are required. Repositories may retain a one-line signpost for humans and agent hosts that do not support the machine-level integration, but the routing procedure must not be duplicated across projects.

## CLI and installation

The CLI is the diagnostic and fallback adapter:

```text
rwl project register <path>
rwl project remove <id>
rwl context --cwd <path> --json
rwl worktree list --project <id> --json
rwl worktree inspect --path <path> --json
rwl launch --path <path> --timeout <seconds> --json
rwl doctor --json
rwl integration install claude
rwl integration remove claude
```

`rwl launch` blocks until the terminal result and exits with a deterministic code. It is a gate holder for a correctly started session, not a babysitter for the session's lifetime.

A per-user installer publishes versioned releases and installs a stable bootstrap executable. The Start Menu shortcut, MCP registration, and session hook point to the stable bootstrap rather than a version-specific executable path.

Claude integration is optional. If Claude Code is installed, the installer can register the user-scoped MCP server and merge the owned session hook. If Claude Code is absent, the desktop launcher remains fully usable, and integration can be installed later through the CLI.

Installation and removal must preserve unrelated MCP servers, hooks, settings, registered projects, and logs unless the user explicitly requests a complete data removal.

## Rhino launch transport

The overall architecture is independent of the final Rhino plug-in discovery mechanism.

The first Rhino 8 candidate was a process-specific environment:

```text
RHINO_PACKAGE_DIRS=<selected worktree output directory>
```

The backend receives the built package directory from the project driver, sets the environment variable only for the new Rhino process, launches Rhino, and waits for the receipt handshake when no conflicting registration exists.

This candidate must be tested against:

- A conventionally installed or registered plug-in with the same GUID.
- Two simultaneous Rhino processes using different worktree builds of the same plug-in.
- Normal Rhino startup after both worktree processes exit.
- Missing, stale, and conflicting output directories.
- Critical dependencies loaded from an unexpected checkout.
- Supported Rhino versions and runtimes.

The same-GUID candidate is unsafe. A fresh August 7, 2026 rerun loaded Natalie's selected linked-worktree binaries and passed the receipt three times, but Rhino silently rewrote the persistent HKLM `PlugIn\\FileName` from the primary checkout to the temporary worktree. Receipt success alone therefore cannot make `RHINO_PACKAGE_DIRS` safe for an already-registered GUID. Natalie declares `windows-registry-lease`, its plug-in GUID, and a demand-load command in the driver result. RWL serializes starts for that GUID behind a cross-process file lock, snapshots every existing HKLM/HKCU `PlugIn\\FileName` value and registry kind, redirects them to the selected `.rhp`, and asks the interactive Windows shell to open the installed bootstrap. That one-shot broker receives only the per-launch environment overrides through a current-user named pipe, starts Rhino with the demand-load command, and returns Rhino's actual PID. RWL verifies the receipt and restores the exact registry values before returning. Already-started Rhino processes can then run concurrently; only their registration-sensitive startup windows serialize.

## README attribution

The README should include a concise acknowledgement near the explanation of Rhino process launching:

> Rhino Worktree Launcher can use Rhino's `RHINO_PACKAGE_DIRS` development mechanism to expose a selected build output to a Rhino process. The `launchSettings.json` configuration used to identify this mechanism was shared by Dale Fugier in the McNeel forum discussion [C# Visual Studio New command in Plugin not recognized](https://discourse.mcneel.com/t/c-visual-studio-new-command-in-plugin-not-recognized/201370/5). Rhino Worktree Launcher is an independent orchestration tool built around project registration, Git worktree selection, launch verification, and human and agent workflows.

The acknowledgement credits the existing Rhino configuration without implying that the launcher, its architecture, or its worktree orchestration originated from that forum example.

## Implementation sequence

### Phase 0: finish the native frontend redesign

- Complete the native WPF visual port without introducing a second backend model.
- Keep current worktree scanning and launch behavior stable until the frontend lands.
- Record the data boundaries that the extracted backend must preserve or deliberately replace.

### Phase 1: stabilize project identity and contracts

- Store the project contract and canonical Git repository identity together in the schema-v4 app-local catalog.
- Import and remove schema-v2 manifest references during one-time catalog migration.
- Define versioned driver request, event, and result DTOs, including the receipt handshake surface and receipt JSON schema.
- Ship the copyable receipt-writer bootstrap template.
- Add fixture repositories and parser tests.

### Phase 2: extract backend application commands

- Move catalog, Git scanning, readiness, and launch coordination out of the UI assembly's event handlers.
- Introduce typed command results and diagnostics.
- Keep the WPF frontend as a thin client.
- Add cancellation and process timeouts.

### Phase 3: add the CLI

- Expose the backend commands through `rwl.exe` with JSON output and deterministic exit codes.
- Verify that `rwl launch` blocks to a terminal result within its timeout and writes a diagnostics log.

### Phase 4: evaluate the Rhino launch transport

- Run the `RHINO_PACKAGE_DIRS` same-GUID and parallel-process experiments.
- Retain receipt handshakes as the source of truth.
- Select one supported launch mechanism per required Rhino version.
- Delete the registration-lease path if the process-specific mechanism replaces it.

### Phase 5: add MCP

- Create a thin local stdio MCP server over the backend commands.
- Install it at Claude Code user scope through the launcher installer or CLI.
- Verify tool discovery, typed results, timeouts, and cancellation.

### Phase 6: add automatic routing

- Add the conditional machine-level session hook.
- Ensure unregistered repositories receive no launcher instructions.
- Ensure compatible but unregistered repositories request explicit registration rather than executing their drivers.

### Phase 7: distribution and migration

- Ship a per-user versioned installer with a stable bootstrap.
- Migrate Natalie's settings into the app-local contract while retaining only its executable driver and receipt integration in the repository.
- Register Natalie against its primary checkout rather than an expendable task worktree.
- Document project onboarding, machine installation, update, rollback, and uninstall behavior.

## Verification gates

The implementation is not complete until all of the following hold:

- One registered repository resolves correctly from its primary checkout and every linked worktree.
- Deleting a linked worktree does not unregister the project.
- The WPF UI, CLI, and MCP return the same worktree state and the same launch result shape.
- A launch result proves the selected `.rhp` and declared dependencies loaded from the selected worktree.
- Overlapping launches do not corrupt Rhino's persistent registration state.
- Repository JSON is never consulted during ordinary catalog, context, worktree, or launch operations.
- Optional GitHub enrichment failure does not hide local worktrees.
- Required build, launch, receipt, and timeout failures remain visible and machine-readable.
- Installation preserves unrelated Claude Code settings and integrations.
- Reinstall and upgrade are idempotent.
- Removing Claude integration leaves the desktop application and project catalog usable.

## Deferred decisions

The following decisions should wait for implementation evidence or a later release:

- Session monitoring, durable operation records, and re-attachable launch observation belong to a future release, if ever.
- Whether non-Claude agent adapters should ship in the first release.
- Whether Rhino 7 requires a separate launch transport.
- Whether project drivers should remain PowerShell or may also be executables.
- Whether the launcher should expose optional repository harnesses beyond build and load verification.
