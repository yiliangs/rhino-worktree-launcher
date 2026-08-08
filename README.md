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

Double-click `Install.bat`, or run:

```powershell
pwsh -NoProfile -File src/RhinoWorktreeLauncher/Install-RhinoWorktreeLauncher.ps1 `
  -ProjectRoot C:\path\to\rhino-worktree-launcher `
  -InstallClaudeIntegration `
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
rwl integration install claude
rwl integration remove claude
```

The desktop Project Settings surface is currently the place to change a saved build mode, replace an imported driver, revoke or restore remote reads, and clear rebuildable RWL caches.

## Build and verify

```powershell
dotnet build RhinoWorktreeLauncher.slnx -c Debug
dotnet test tests/RhinoWorktreeLauncher.Tests/RhinoWorktreeLauncher.Tests.csproj
```

The WPF application remains a fixed 720 x 1000 native surface with embedded IBM Plex Sans and Geist Mono fonts. Backend code is in `RhinoWorktreeLauncher.Core`; WPF, CLI, and MCP projects contain presentation or transport logic only.
