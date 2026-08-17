# Rhino Worktree Launcher

Rhino Worktree Launcher is a native Windows tool for registering Rhino plug-in projects, inspecting their Git worktrees, and launching the exact selected `.rhp` with loaded-binary verification. A shared .NET 8 backend serves the WPF desktop application, `rwl` CLI, and local stdio MCP server.

## Project and build model

RWL uses the selected Git worktree as the source of truth. It never creates a second source or build tree. Project Config stores one Rhino plug-in project, Visual Studio solution, solution Configuration and Platform, plus the desktop launch-mode default for the whole registered Git project. If a repository contains multiple Rhino plug-in projects, Config presents them for an explicit choice instead of guessing from existing `.rhp` files. RWL reopens the same relative project and solution in the selected worktree and verifies the selection before launch.

The two launch modes are:

- **Build & Launch** (default): run `dotnet build` on the selected solution and configuration in the selected worktree, evaluate the plug-in project's mapped `TargetPath`, then launch that `.rhp`.
- **Direct Launch**: evaluate the same `TargetPath` and load the existing `.rhp` without building or making a freshness claim.

The desktop action follows the default saved in Config. MCP agents choose per request by calling `rhino_worktree_build_and_launch` or `rhino_worktree_launch_existing`; the MCP tools never inherit the desktop default.

All ordinary build behavior remains owned by the solution and its MSBuild settings, including project imports, output paths, pre-build and post-build targets, and configuration mapping. RWL does not substitute a project-only build, copy the sources, reroute caches, or run an imported driver.

Adding a project grants access to its Git worktrees and, by default, remote-status access. A Build & Launch operation can modify ordinary solution outputs in the selected worktree. The remote grant is optional and remains editable in Config.

RWL-owned state remains under `%LOCALAPPDATA%\RhinoWorktreeLauncher`:

- `projects.json`: project grants and canonical solution selections
- `remotes\<project-id>.git`: remote mirror used for ahead/behind calculation
- `launches`: verifier request and response state
- `logs`: terminal launch diagnostics

Refresh uses read-only Git commands against the project. It does not fetch into the repository or update repository refs.

## Launch and verification

A launch performs these steps:

1. Resolve the registered project and exact Git worktree.
2. Revalidate the saved solution and configuration in that worktree.
3. Build the selected solution when the mode is Build & Launch, or skip the build in Direct Launch.
4. Ask MSBuild for the plug-in project's mapped `TargetPath` and require that exact `.rhp` to exist.
5. Apply a serialized temporary current-user registration that names the selected `.rhp` and loads it at startup.
6. Start Rhino. That registration is the only loading mechanism, and the `.rhp` is not passed on Rhino's command line.
7. Wait for the launched Rhino process to map the selected `.rhp` into its address space.
8. Fail closed unless that exact file is in use, then restore every registry value the launch wrote.

If a machine-wide registration claims the same plug-in ID and names a different file, for example an all-users install or a registration left by debugging another checkout, Rhino resolves the duplicate ID to that machine-wide file. Where your account holds write access to the machine `Plug-ins` key, granted once with an elevated account, the launch suspends that registration: it is journaled to disk, removed for the launch, and restored when the launch ends, so ordinary Rhino sessions keep the installed copy. A journal left by a crashed launch is restored by the next launch of the same plug-in. Without that access the launch refuses before Rhino starts and names the exact key, since RWL never elevates; grant the access or remove the key if it is stale. An existing current-user registration never blocks a launch: the temporary registration replaces it and restores it afterward.

To grant the access, run once from an elevated PowerShell:

```powershell
$path = 'HKLM:\SOFTWARE\McNeel\Rhinoceros\8.0\Plug-ins'
$acl = Get-Acl $path
$acl.AddAccessRule([System.Security.AccessControl.RegistryAccessRule]::new(
    "$env:USERDOMAIN\$env:USERNAME", 'FullControl', 'ContainerInherit', 'None', 'Allow'))
Set-Acl $path $acl
```

The launched plug-in does not need to expose an RWL command, callback, or receipt writer. A build receipt is not used to infer freshness.

## Desktop configuration

The main window separates two scopes:

- **Config** is project-specific. It selects the Rhino plug-in project, solution, Configuration and Platform, Build & Launch or Direct Launch, remote reads, and remote-cache clearing.
- **Settings** is global. It contains MCP setup for Claude Code and Codex.

The primary action follows Config and reads **Build & Launch** or **Launch Rhino**.

## Install

### End users

Download `RhinoWorktreeLauncher-<version>-win-x64.zip` from a GitHub release, extract it, and double-click `Install.bat`. The package is self-contained, so the user does not need the .NET SDK. It installs a versioned payload and the stable `%LOCALAPPDATA%\RhinoWorktreeLauncher\bootstrap\rwl.exe`, then opens the desktop application.

Each release publishes a SHA-256 checksum beside the archive. The binaries are not yet Authenticode-signed, so Windows SmartScreen may warn until a signing certificate is added to the release pipeline.

Use **Settings** in the desktop application to configure Claude Code or Codex. RWL updates only its owned client entry, creates a `.rwl-backup` beside an existing client configuration before changing it, and reports whether the stable bootstrap is available. Restart the client after setup.

### Developers

From a source checkout, double-click `Install.bat`, or run:

```powershell
pwsh -NoProfile -File src/RhinoWorktreeLauncher/Install-RhinoWorktreeLauncher.ps1 `
  -ProjectRoot C:\path\to\rhino-worktree-launcher `
  -InstallClaudeIntegration `
  -InstallCodexIntegration `
  -Launch
```

Source installation first produces the same installable payload as a release package, then installs that payload. `eng/New-RwlPackage.ps1` is the only binary producer; the installer never maintains a second publish path.

Each update publishes a versioned release under `%LOCALAPPDATA%\RhinoWorktreeLauncher\releases`. The Start Menu shortcut, CLI, MCP registration, and Claude hook target the stable bootstrap. The bootstrap reads `current.json`, so integrations do not contain version-specific paths.

## CLI

```text
rwl project register <path> [--plugin-project <path>] [--solution <path>] [--configuration <name> --platform <name>] [--direct] [--no-remote]
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

Claude Code can optionally receive a SessionStart message resolving the exact registered worktree. The MCP server independently publishes tool descriptions, JSON schemas, server instructions, side-effect annotations, cancellation behavior, and backend-enforced project grants. Its separate build-and-launch and launch-existing tools make the per-request build choice explicit while preserving RWL verification in both paths.

## Build and verify

```powershell
dotnet build RhinoWorktreeLauncher.slnx -c Debug
dotnet test tests/RhinoWorktreeLauncher.Tests/RhinoWorktreeLauncher.Tests.csproj
dotnet test tests/RhinoWorktreeLauncher.UiTests/RhinoWorktreeLauncher.UiTests.csproj
```

To produce the same self-contained package used by releases:

```powershell
pwsh -NoProfile -File eng/New-RwlPackage.ps1 `
  -OutputPath artifacts/RhinoWorktreeLauncher `
  -Version 1.0.0
```

The WPF application remains a fixed 720 x 1000 native surface with embedded IBM Plex Sans and Geist Mono fonts. Backend code is in `RhinoWorktreeLauncher.Core`; WPF, CLI, and MCP projects contain presentation or transport logic only.
