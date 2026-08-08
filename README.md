# Rhino Worktree Launcher

Rhino Worktree Launcher is a native Windows tool for registering Rhino plug-in projects, inspecting their Git worktrees, and launching an exact selected build with loaded-binary verification. A shared .NET 8 backend serves the WPF desktop application, `rwl` CLI, and local stdio MCP server.

## Repository-isolation model

Repositories are read-only inputs to RWL. Adding a project grants project-wide read access and, by default, remote-status access. The remote grant is optional and remains editable in Project Settings.

RWL writes all of its state under `%LOCALAPPDATA%\RhinoWorktreeLauncher`:

- `projects.json`: access grants and typed build profiles
- `projects\<project-id>\drivers`: imported custom-driver copies
- `workspaces\<project-id>\<worktree-id>`: source snapshots, persistent build trees, caches, temporary files, and verification requests
- `remotes\<project-id>.git`: RWL-owned remote mirror used for ahead/behind calculation
- `logs`: terminal launch diagnostics

Refresh uses read-only Git commands against the project. It does not fetch into the repository or update its refs. RWL snapshots tracked files plus nonignored untracked files and performs restore/build only in its own workspace. Existing ignored build outputs such as `bin`, `obj`, and `node_modules` are not copied from the repository.

This is application-enforced isolation, not an operating-system security sandbox. A deliberately hostile build target or imported script could still address paths outside its working directory. RWL itself never asks those tools to write to the repository.

## Build setup

The default path is a detected, typed build profile stored in `projects.json`. RWL detects the Rhino plug-in project, runtime, plug-in GUID, npm restore roots, .NET build target, output `.rhp`, and critical project dependencies. The same profile applies to every worktree because it operates on the selected worktree's app-owned snapshot.

The escape hatch is **Import my own driver**. The selected PowerShell file is copied into RWL storage immediately; later launches do not depend on the original file. Project Settings can switch between a freshly detected profile and an imported driver or replace the imported copy. See [imported driver protocol v2](docs/imported-driver-protocol-v2.md).

## Launch and verification

A launch performs these steps:

1. Resolve the registered project and Git worktree identity.
2. Reconcile the worktree's persistent app-owned source snapshot and build tree.
3. Run the typed build profile or imported driver in the build tree with app-local NuGet, npm, .NET, and temporary paths.
4. Apply a serialized temporary current-user Rhino plug-in path overlay.
5. Start Rhino with RWL's bundled verifier plug-in.
6. Have the verifier load the target GUID and report the actual `.rhp` and critical assembly paths.
7. Fail closed unless every loaded path exactly matches the prepared workspace artifacts, then restore the prior registration value.

The launched plug-in does not need to expose an RWL command, callback, or receipt writer. Launch verification files remain in the RWL workspace.

## Install

### End users

Download `RhinoWorktreeLauncher-<version>-win-x64.zip` from a GitHub release, extract it, and double-click `Install.bat`. The package is self-contained, so the user does not need the .NET SDK. It installs a versioned payload and the stable `%LOCALAPPDATA%\RhinoWorktreeLauncher\bootstrap\rwl.exe`, then opens the desktop application.

Each release publishes a SHA-256 checksum beside the archive. The binaries are not yet Authenticode-signed, so Windows SmartScreen may warn until a signing certificate is added to the release pipeline.

Use **MCP setup** in the desktop application to configure Claude Code or Codex. RWL updates only its owned client entry, creates a `.rwl-backup` beside an existing client configuration before changing it, and reports whether the stable bootstrap is available. Restart the client after setup.

### Developers

From a source checkout, double-click `Install.bat`, or run:

```powershell
pwsh -NoProfile -File src/RhinoWorktreeLauncher/Install-RhinoWorktreeLauncher.ps1 `
  -ProjectRoot C:\path\to\rhino-worktree-launcher `
  -InstallClaudeIntegration `
  -InstallCodexIntegration `
  -Launch
```

Each update publishes a versioned release under `%LOCALAPPDATA%\RhinoWorktreeLauncher\releases`. The Start Menu shortcut, CLI, MCP registration, and Claude hook target the stable `%LOCALAPPDATA%\RhinoWorktreeLauncher\bootstrap\rwl.exe`. The bootstrap reads `current.json`, so integrations do not contain version-specific paths.

## CLI

```text
rwl project register <path> [--no-remote]
rwl project remove <id>
rwl context --cwd <path> --json
rwl worktree list --project <id> [--local-only] --json
rwl worktree inspect --path <path> --json
rwl launch --path <path> --timeout <seconds> --json
rwl doctor --json
rwl integration status [claude|codex] --json
rwl integration install <claude|codex> [--no-session-context]
rwl integration remove <claude|codex>
```

Claude Code can optionally receive a SessionStart message resolving the exact registered worktree. This hook supplies situational context only. The MCP server independently publishes tool descriptions, JSON schemas, server instructions, side-effect annotations, cancellation behavior, and backend-enforced project grants. Codex uses the same stable MCP bootstrap and receives a 300-second tool timeout for verified Rhino launches.

The desktop Project Settings surface is currently the place to change a saved build mode, replace an imported driver, revoke or restore remote reads, and clear rebuildable RWL caches.

## Build and verify

```powershell
dotnet build RhinoWorktreeLauncher.slnx -c Debug
dotnet test tests/RhinoWorktreeLauncher.Tests/RhinoWorktreeLauncher.Tests.csproj
```

To produce the same self-contained package used by releases:

```powershell
pwsh -NoProfile -File eng/New-RwlPackage.ps1 `
  -OutputPath artifacts/RhinoWorktreeLauncher `
  -Version 1.0.0
```

The WPF application remains a fixed 720 x 1000 native surface with embedded IBM Plex Sans and Geist Mono fonts. Backend code is in `RhinoWorktreeLauncher.Core`; WPF, CLI, and MCP projects contain presentation or transport logic only.
