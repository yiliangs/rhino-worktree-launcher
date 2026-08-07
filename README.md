# Rhino Worktree Launcher

Rhino Worktree Launcher is a native Windows tool for registering Rhino plug-in repositories, inspecting their Git worktrees, and launching one selected build with loaded-binary verification. One .NET 8 backend serves the WPF desktop application, the `rwl` CLI, and a local stdio MCP server.

RWL is a one-off bootstrapper. A launch performs repository preflight/build work, starts Rhino, verifies the loaded `.rhp` and critical dependencies, returns one terminal result, and stops observing the process. It does not monitor Rhino sessions or retain launch state beyond diagnostics logs.

## Project contract

An adopting repository commits `.rhino-worktree-launcher.json` and a repository-owned driver:

```json
{
  "schemaVersion": 2,
  "projectId": "example-plugin",
  "displayName": "Example Plugin",
  "driver": {
    "protocolVersion": 1,
    "entrypoint": "tools/rhino-worktree/Driver.ps1"
  },
  "launch": {
    "rhinoVersion": 8,
    "mode": "rhino-package-directory"
  }
}
```

The selected worktree's driver owns project-specific preflight, build, artifact discovery, critical-dependency declaration, and receipt configuration. Registration is the machine trust decision: it permits RWL to execute that repository's driver and build scripts from every linked worktree.

See [driver protocol v1](docs/driver-protocol-v1.md), the copyable [PowerShell driver template](templates/Driver.ps1), and the dependency-free [.NET Framework receipt writer](templates/WorktreeLaunchReceiptBootstrap.cs).

## Launch isolation and verification

For Rhino 8, RWL sets `RHINO_PACKAGE_DIRS` only on the new Rhino process. It does not edit persistent plug-in registration. Repository-owned code inside the plug-in writes a receipt after load; RWL fails closed unless the launch ID, Rhino PID, `.rhp` path, and every declared critical dependency match the selected worktree's driver result.

Rhino Worktree Launcher can use Rhino's `RHINO_PACKAGE_DIRS` development mechanism to expose a selected build output to a Rhino process. The `launchSettings.json` configuration used to identify this mechanism was shared by Dale Fugier in the McNeel forum discussion [C# Visual Studio New command in Plugin not recognized](https://discourse.mcneel.com/t/c-visual-studio-new-command-in-plugin-not-recognized/201370/5). Rhino Worktree Launcher is an independent orchestration tool built around project registration, Git worktree selection, launch verification, and human and agent workflows.

## Install

Double-click `Install.bat`, or run:

```powershell
pwsh -NoProfile -File src/RhinoWorktreeLauncher/Install-RhinoWorktreeLauncher.ps1 `
  -ProjectRoot C:\path\to\plugin-repository `
  -InstallClaudeIntegration `
  -Launch
```

Each update publishes a versioned release under `%LOCALAPPDATA%\RhinoWorktreeLauncher\releases\`. The Start Menu shortcut, CLI, MCP registration, and Claude session hook target the stable `%LOCALAPPDATA%\RhinoWorktreeLauncher\bootstrap\rwl.exe`. The stable bootstrap resolves `current.json`, so upgrades do not leave version-specific paths in integrations. Catalog data and diagnostics remain outside versioned releases.

Omit `-InstallClaudeIntegration` when only the desktop application is wanted. Integration can be installed or removed later without touching registered projects, logs, or the desktop application:

```powershell
rwl integration install claude
rwl integration remove claude
```

Installation merges one user-scoped MCP server and one conditional `SessionStart` hook. Existing Claude settings, hooks, and MCP servers are preserved. Unrelated directories receive no context. A compatible but unregistered repository receives registration guidance, and its driver is not executed until registration establishes trust.

## CLI

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

`rwl launch` blocks through build, Rhino startup, and receipt verification. Required failures use stable diagnostic codes and a nonzero exit code. Every launch writes JSONL diagnostics under `%LOCALAPPDATA%\RhinoWorktreeLauncher\logs\`.

## MCP tools

The optional user-scoped stdio server exposes:

- `rhino_worktree_resolve_context`
- `rhino_worktree_list_worktrees`
- `rhino_worktree_inspect`
- `rhino_worktree_launch`
- `rhino_worktree_doctor`

The launch tool is blocking and returns the same typed terminal result as the CLI and WPF application. Tools resolve the supplied path on every call, so moving between linked worktrees does not rely on stale session state.

## Build and verify

```powershell
dotnet build RhinoWorktreeLauncher.slnx -c Debug
dotnet test tests/RhinoWorktreeLauncher.Tests/RhinoWorktreeLauncher.Tests.csproj
```

The WPF application remains a fixed 720 × 1000 native surface with embedded IBM Plex Sans and Geist Mono fonts. Backend code is in `RhinoWorktreeLauncher.Core`; the WPF, CLI, and MCP projects contain presentation or transport logic only.

## Update, rollback, and removal

Re-run the installer to publish a new release and atomically advance `current.json`. To roll back, replace `current.json` with paths to an earlier directory under `releases`. To remove Claude integration first run `rwl integration remove claude`; this intentionally leaves the catalog and logs intact. A complete uninstall may then remove the Start Menu shortcut, stable bootstrap, and versioned releases. Remove `%LOCALAPPDATA%\RhinoWorktreeLauncher\projects.json` or `logs` only when explicitly discarding those data.
