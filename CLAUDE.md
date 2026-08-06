# Rhino Worktree Launcher

Independent .NET 8 WPF developer utility for launching Rhino plug-in repositories from Git worktrees.

## Architecture

- `ProjectManifest` owns the versioned `.rhino-worktree-launcher.json` contract.
- `ProjectCatalog` stores only local manifest paths under `%LOCALAPPDATA%\RhinoWorktreeLauncher\projects.json`.
- `GitWorktreeScanner` discovers non-prunable worktrees and evaluates manifest-declared readiness.
- `WorktreeLaunchService` launches normal Rhino for the primary checkout or the repository-owned worktree entry point for linked worktrees.
- The application never edits Rhino registration and never infers a plug-in's build or verification protocol.

## Build and install

```powershell
dotnet build src/RhinoWorktreeLauncher/RhinoWorktreeLauncher.csproj -c Debug
pwsh -NoProfile -File src/RhinoWorktreeLauncher/Install-RhinoWorktreeLauncher.ps1 -Launch
```

The installer publishes non-destructive versioned releases under `%LOCALAPPDATA%\RhinoWorktreeLauncher\releases\` and updates one Start Menu shortcut.

## UI

Use `src/RhinoWorktreeLauncher/Assets/rhino-launcher.png` in the header and `rhino-launcher.ico` for the executable. Keep the interface a compact native utility with flat rows and restrained hierarchy.

For single-file WPF publishing, use `<ApplicationIcon>` for the taskbar icon and a WPF `Resource` for the header PNG. Do not set `Window.Icon` to a linked external path; published startup fails with `XamlParseException`.
