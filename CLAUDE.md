# Rhino Worktree Launcher

Independent .NET 8 Windows utility with a thin WPF/WebView2 host and a local HTML/CSS/JavaScript interface for launching Rhino plug-in repositories from Git worktrees.

## Architecture

- `ProjectManifest` owns the versioned `.rhino-worktree-launcher.json` contract.
- `ProjectCatalog` stores only local manifest paths under `%LOCALAPPDATA%\RhinoWorktreeLauncher\projects.json`.
- `GitWorktreeScanner` discovers non-prunable worktrees, local diff and divergence metadata, and optional authenticated GitHub PR state.
- `MainWindow` is a thin WebView2 host. It owns native dialogs, theme forwarding, backend execution, and the JSON message bridge defined by `LauncherDtos`.
- `Web/` owns the complete interface. It is framework-free HTML/CSS/JavaScript and includes a mock-data fallback for browser rendering and behavioral checks.
- `WorktreeLaunchService` launches normal Rhino for the primary checkout or the repository-owned worktree entry point for linked worktrees.
- The application never edits Rhino registration and never infers a plug-in's build or verification protocol.

## Build and install

```powershell
dotnet build src/RhinoWorktreeLauncher/RhinoWorktreeLauncher.csproj -c Debug
pwsh -NoProfile -File src/RhinoWorktreeLauncher/Install-RhinoWorktreeLauncher.ps1 -Launch
```

The installer publishes non-destructive versioned releases under `%LOCALAPPDATA%\RhinoWorktreeLauncher\releases\` and updates one Start Menu shortcut.

## UI

Use `src/RhinoWorktreeLauncher/Assets/rhino-launcher.png` in the header and `rhino-launcher.ico` for the executable. The fixed 720 × 1000 interface follows the Windows app theme live, uses bundled IBM Plex Sans and Geist Mono, and keeps local status, tracked-line diff, PR state, activity, and default-branch divergence in one two-line worktree row. Refresh presents independent LOCAL and GIT progress in its fixed 148 × 50 control.

Published releases are self-contained and multifile. Use `<ApplicationIcon>` for the taskbar icon and a WPF `Resource` for the native loading PNG. Do not set `Window.Icon` to a linked external path; published startup fails with `XamlParseException`. WebView2 uses the stable `%LOCALAPPDATA%\RhinoWorktreeLauncher\WebView2` user-data folder across versioned releases, and the last launcher snapshot is restored before background Git enrichment.

Startup verification must distinguish native window visibility, WebView content availability, and fresh local-scan completion. Do not treat WebView child-process creation as first paint. On the owner machine, the stable profile and multifile publish did not materially reduce the roughly 3.2-second warm WebView startup, so preserve the native loading surface and cached rows rather than claiming that browser initialization was eliminated.
