# Rhino Worktree Launcher

Native Windows tool for registering Rhino plug-in projects, inspecting their Git worktrees, and launching the exact selected `.rhp` with loaded-binary verification. One .NET 8 backend serves the WPF desktop, `rwl` CLI, and stdio MCP server.

![The Rhino Worktree Launcher main window. A project drop-down reading "Acme Panelizer" sits above the repository path, and a rail below it lists six Git worktrees, each with its launch mode, its uncommitted line counts, and how long ago it was touched. Settings, Open folder, and Build and Launch run along the bottom.](docs/images/main-window.png)

*The desktop surface. Every project and branch name shown is invented.*

## Requirements

- Windows x64 and Rhino 8. `Rhino.exe` resolves from that version's default install directory. Rhino 7 and Rhino 9 are not supported yet.
- Git and the .NET SDK on `PATH`, the SDK your plug-in solution needs. `rwl doctor` checks both.
- A repository with at least one Rhino plug-in project. Registration refuses one with none.

## Project and build model

The selected Git worktree is the source of truth; RWL never creates a second source or build tree. Config stores one plug-in project, one solution, its Configuration and Platform, and the desktop launch-mode default, per registered Git project. Where a repository holds more than one plug-in project, Config asks instead of guessing from existing `.rhp` files. The same relative project and solution reopen in whichever worktree is selected.

Two launch modes:

- **Build & Launch** (default): `dotnet build` the selected solution and configuration in the selected worktree, evaluate the plug-in project's mapped `TargetPath`, launch that `.rhp`.
- **Direct Launch**: evaluate the same `TargetPath` and load the existing `.rhp`, without building or claiming freshness.

The desktop footer offers both, one button each. The Config default is what the worktree row's mode chip reports and what Enter on the list and the CLI pass. MCP agents choose per request with `rhino_worktree_build_and_launch` or `rhino_worktree_launch_existing`, never inheriting that default.

Your solution and its MSBuild settings own all build behavior: imports, output paths, pre- and post-build targets, configuration mapping. RWL never substitutes a project-only build, copies sources, reroutes caches, or reads a driver or configuration file from your repository. Your plug-in needs no RWL command, callback, or receipt writer, and no receipt is used to infer freshness.

Registering a project grants access to its worktrees and, by default, remote status. Build & Launch can modify that worktree's ordinary solution outputs. The remote grant is optional and editable in Config. Refresh runs read-only Git, never fetching into the repository or moving its refs.

State lives under `%LOCALAPPDATA%\RhinoWorktreeLauncher`:

- `projects.json`: grants and canonical solution selections
- `remotes\<project-id>.git`: mirror for ahead/behind
- `locks`: the per (Rhino version, plug-in ID) registration lock and journal
- `logs`: launch diagnostics, and the executor's own record

## Launch and verification

A launch succeeds only once the Rhino it started holds the selected `.rhp` mapped in its address space. Process creation is not success, and an unverified Rhino is killed at the timeout.

1. Resolve the registered project and exact worktree.
2. Revalidate the saved solution and configuration there.
3. Build, or skip the build in Direct Launch.
4. Ask MSBuild for the plug-in project's mapped `TargetPath` and require that exact `.rhp`.
5. Start a launch executor through the interactive Windows shell. Everything below runs there, never in the desktop, CLI, or MCP server that asked.
6. Journal both registry hives for that plug-in ID, then displace: the current-user key is cleared and rewritten as Rhino's documented install seed naming the selected `.rhp`, carrying the load mode the displaced registration recorded so a startup plug-in still loads at startup. Where a machine registration already names that `.rhp`, the key is cleared and left empty, since Rhino loads it from there.
7. Confirm from a separate process that the registration is really there, and stop before starting Rhino if it is not.
8. Start Rhino. That registration is the only loading mechanism, and the `.rhp` never goes on Rhino's command line.
9. Wait for that Rhino to map the selected `.rhp`.
10. Fail closed unless that exact file is in use, then restore both hives from the journal.
11. Linger detached until Rhino exits, restore again if Rhino rewrote either hive, then delete the journal.

Steps 5 and 7: a launcher host can run with its current-user registry writes intercepted, reading its own seed back while the registry Rhino reads never receives it. A shell-started process is outside that interception, and only an independent reader can catch it, so the launch fails in seconds with `registry_seed_not_visible` instead of at the verification timeout. A host that cannot reach the shell at all says so at startup and fails every launch with `interactive_spawn_unavailable`. Both codes name the host, not the worktree, so rerun from outside that process chain: `rwl launch --path <worktree>` in an ordinary terminal, or the desktop. `rwl doctor` checks on demand, and [ADR 0015](docs/adr/0015-mutate-registrations-only-from-an-interactive-launch-executor.md) records the observation behind it.

Step 11: Rhino writes the artifact it loaded back into its registration, after the launch that started it has restored and returned. Without the detached wait, a worktree path stays registered for ordinary Rhino sessions. The journal is written before anything is touched and deleted only once Rhino is gone, so a killed launch cannot strand its seed: the next launch of the same plug-in restores the journal first.

Every failure carries a named diagnostic code for the step that failed, and a launch queued behind another session's lock names the holder rather than expiring unexplained. Each launch writes JSONL under `logs`, and the executor writes its own beside it, named in the first.

A launch can carry caller-supplied environment variables into the Rhino it starts, for in-Rhino harnesses that arm on an environment read: MCP takes a map, the CLI one `--env NAME=VALUE`. They are scoped to that process, and `RWL_` names are refused because that prefix carries the launch identity.

### The build Rhino loads outside RWL

Between launches, Rhino resolves the plug-in ID from the standing registration: the machine hive if it holds one, otherwise the current-user hive, in the installed shape or the seed shape. The worktree whose tree contains that file is the registered one, by longest path match, and the desktop marks it DEFAULT beside the PRIMARY chip that names the repository's main working tree. They are different facts and a row can carry either, both, or neither.

`rwl registration set --path <worktree>` rewrites that registration to the worktree's canonical artifact, so an ordinary Rhino start loads that build. It never builds: the artifact has to be there already, and a missing one fails the way a direct launch does, before anything is written. The write itself runs in the launch executor like every other registry mutation, in place into whichever value the existing registration uses, and an independent process confirms it before the change is reported. It refuses while a launch of the same plug-in still has a journal pending, because that launch's restore would undo it; close that Rhino, or launch again so RWL restores it, then retry. The desktop offers the same change as SET DEFAULT on the selected row. [ADR 0016](docs/adr/0016-switch-the-standing-registration-through-the-launch-executor.md) records the decision.

### A competing machine-wide registration

A machine-wide registration for the same plug-in ID naming a different file, say an all-users install or one left from debugging another checkout, wins: Rhino resolves the duplicate ID to that file. With write access to the machine `Plug-ins` key, granted once from an elevated account, the launch displaces and restores that one too, so ordinary sessions keep the installed copy. Without that access the launch refuses before Rhino starts and names the key, since RWL never elevates; grant access, or remove the key if it is stale. A machine registration already naming the selected `.rhp` is not a conflict, and a current-user registration never blocks a launch: it is captured whole, displaced, and restored.

Grant it once from an elevated PowerShell:

```powershell
$path = 'HKLM:\SOFTWARE\McNeel\Rhinoceros\8.0\Plug-ins'
$acl = Get-Acl $path
$acl.AddAccessRule([System.Security.AccessControl.RegistryAccessRule]::new(
    "$env:USERDOMAIN\$env:USERNAME", 'FullControl', 'ContainerInherit', 'None', 'Allow'))
Set-Acl $path $acl
```

## Desktop configuration

- **Config** is per project: plug-in project, solution, Configuration and Platform, Build & Launch or Direct Launch, remote reads, remote-cache clearing.
- **Settings** is global: MCP setup for Claude Code and Codex.

The footer offers both modes: **Launch** loads the existing artifact and **Build & Launch** builds first. The Config default is what the row's mode chip reports and what Enter on the worktree list passes.

## Install

### End users

Download `RhinoWorktreeLauncher-<version>-win-x64.zip` from a release, extract it, and double-click `Install.bat`. It installs a versioned payload plus the stable `%LOCALAPPDATA%\RhinoWorktreeLauncher\bootstrap\rwl.exe`, then opens the desktop. `--no-shortcut` skips the Start Menu entry.

Nothing beyond Windows is required. The package is self-contained, so no .NET SDK, and its own `rwl.exe` performs the install, so no PowerShell and no execution policy to change.

Each release publishes a SHA-256 checksum beside the archive. Binaries are not yet Authenticode-signed, so SmartScreen may warn until signing is added.

Configure Claude Code or Codex from **Settings**. RWL touches only its own client entry, backs up an existing client configuration to `.rwl-backup` first, and reports whether the stable bootstrap is available. Restart the client afterward.

### Developers

From a source checkout, double-click `Install.bat`, or run:

```powershell
pwsh -NoProfile -File src/RhinoWorktreeLauncher/Install-RhinoWorktreeLauncher.ps1 `
  -ProjectRoot C:\path\to\rhino-worktree-launcher `
  -InstallClaudeIntegration `
  -InstallCodexIntegration `
  -Launch
```

Source install asks `eng/New-RwlPackage.ps1`, the only producer of installable binaries, for the same payload a release contains, then installs it.

Each update publishes a versioned release under `%LOCALAPPDATA%\RhinoWorktreeLauncher\releases`. Shortcut, CLI, MCP registration, and Claude hook all target the stable bootstrap, which reads `current.json`, so integrations hold no version-specific paths.

## CLI

```text
rwl project register <path> [--plugin-project <path>] [--solution <path>] [--configuration <name> --platform <name>] [--direct] [--no-remote] [--json]
rwl project remove <id> [--json]
rwl context --cwd <path> [--json]
rwl worktree list --project <id> [--local-only] [--json]
rwl worktree inspect --path <path> [--json]
rwl launch --path <path> [--timeout <seconds>] [--env <NAME=VALUE>] [--json]
rwl registration set --path <path> [--json]
rwl rhino instances [--json]
rwl doctor [--json]
rwl integration status [claude|codex] [--json]
rwl integration install <claude|codex> [--bootstrap <path>] [--no-session-context] [--json]
rwl integration remove <claude|codex> [--json]
```

`rwl --help` prints the same list, generated from the parser.

`rwl rhino instances` lists every live Rhino with its start time and the plug-in artifacts it holds mapped, read the way a launch verifies its own. Concurrent launches legitimately leave several verified Rhinos running, each a different build, so this is how a caller without a launch result's `rhinoProcessId` picks one. A Rhino this account cannot read is listed as unattributable with the reason, not omitted. MCP calls `rhino_worktree_attribution` for the same answer; `rwl doctor` includes it beside its process inventory.

`rwl doctor` also lists live RWL processes: role, release directory, start time, parent alive. Since a server ends with the session that started it, two states warn: a parentless server, which can serve nobody, and one serving a release the installation has replaced.

Claude Code can optionally receive a SessionStart message naming the exact registered worktree. The MCP server's separate build-and-launch and launch-existing tools make the per-request build choice explicit, with RWL verification governing both. Grants are backend-enforced: a path no registered project covers is refused with `project_not_registered`.

## Build and verify

```powershell
dotnet build RhinoWorktreeLauncher.slnx -c Debug
dotnet test tests/RhinoWorktreeLauncher.Tests/RhinoWorktreeLauncher.Tests.csproj
dotnet test tests/RhinoWorktreeLauncher.UiTests/RhinoWorktreeLauncher.UiTests.csproj
```

Package exactly as releases do:

```powershell
pwsh -NoProfile -File eng/New-RwlPackage.ps1 `
  -OutputPath artifacts/RhinoWorktreeLauncher `
  -Version 1.0.0
```

The WPF app is a fixed 720 x 1000 native surface with embedded IBM Plex Sans and Geist Mono. Backend lives in `RhinoWorktreeLauncher.Core`; WPF, CLI, and MCP hold presentation or transport only.

## License

MIT. See [LICENSE](LICENSE).

The embedded IBM Plex Sans and Geist Mono files are not covered by it. They remain under the SIL Open Font License 1.1, with full text and attribution in [`src/RhinoWorktreeLauncher/Assets/Fonts/LICENSES.txt`](src/RhinoWorktreeLauncher/Assets/Fonts/LICENSES.txt).
