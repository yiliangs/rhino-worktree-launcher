# Rhino Worktree Launcher

Independent native .NET 8 WPF utility for launching Rhino plug-in repositories from Git worktrees.

## Architecture

- `ProjectManifest` owns the versioned `.rhino-worktree-launcher.json` contract.
- `ProjectCatalog` stores only local manifest paths under `%LOCALAPPDATA%\RhinoWorktreeLauncher\projects.json`.
- `GitWorktreeScanner` discovers non-prunable worktrees, local diff and divergence metadata, and optional authenticated GitHub PR state.
- `MainWindow` owns the native WPF interface, live Windows theme response, refresh coordination, dialogs, and backend execution.
- `TrackingTextBlock` supplies the letter spacing that WPF text controls do not expose and explicitly honors the inherited WPF text-formatting mode; `InlineIdentityPanel` keeps identity badges adjacent to a branch name without sacrificing trimming; `InsetHighlightBorder` renders the restrained inner highlight on raised controls and chips.
- `WorktreeLaunchService` launches normal Rhino for the primary checkout or the repository-owned worktree entry point for linked worktrees.
- The application never edits Rhino registration and never infers a plug-in's build or verification protocol.

## Build and install

```powershell
dotnet build src/RhinoWorktreeLauncher/RhinoWorktreeLauncher.csproj -c Debug
pwsh -NoProfile -File src/RhinoWorktreeLauncher/Install-RhinoWorktreeLauncher.ps1 -Launch
```

The installer publishes non-destructive versioned releases under `%LOCALAPPDATA%\RhinoWorktreeLauncher\releases\` and updates one Start Menu shortcut.

## UI

Use `src/RhinoWorktreeLauncher/Assets/rhino-launcher.png` in the header and `rhino-launcher.ico` for the executable. The fixed 720 × 1000 interface follows the Windows app theme live, embeds the same Google Fonts IBM Plex Sans and Geist Mono variable binaries as the design handoff, and keeps local status, tracked-line diff, PR state, activity, and default-branch divergence in one two-line worktree row. Keep WPF text on Ideal metrics with Fixed hinting and ClearType antialiasing; at 150% DPI this most closely reproduces Chromium's fractional LCD coverage. Treat 720 × 1000 as the DWM-visible frame rather than WPF's larger window rectangle with invisible resize borders; crop captures to `DWMWA_EXTENDED_FRAME_BOUNDS`. Use only the shared 4, 8, and 14 corner-radius tokens for labels, controls/rows, and the main panel respectively. The worktree scrollbar owns a dedicated 12-unit right rail balanced by a 12-unit left spacer; row backgrounds and textures must terminate before that rail, never render beneath it. Refresh presents independent LOCAL and GIT progress in its fixed 148 × 50 control.

Published releases are self-contained and multifile to avoid single-file extraction and mapping overhead. Use `<ApplicationIcon>` for the taskbar icon and WPF `Resource` items for bundled images and fonts. Do not set `Window.Icon` to a linked external path; published startup fails with `XamlParseException`.

Startup verification must distinguish native window visibility from completion of local scanning and optional Git/GitHub enrichment. The interface must remain usable when fetch or authenticated `gh` lookup fails.
