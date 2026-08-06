# Rhino Worktree Launcher

A compact Windows utility for launching Rhino plug-ins from isolated Git worktrees without leaving the machine-wide plug-in registration pointed at a task branch.

## Project contract

Supported repositories expose a committed `.rhino-worktree-launcher.json` at their root. The launcher reads that manifest, scans `git worktree list --porcelain`, and delegates linked worktrees to the repository-owned entry point. It does not infer plug-in GUIDs, build commands, or verification rules.

```json
{
  "schemaVersion": 1,
  "projectId": "example-plugin",
  "displayName": "Example Plugin",
  "primaryLaunch": {
    "mode": "normal-rhino",
    "rhinoVersion": 8
  },
  "worktreeLaunch": {
    "entrypoint": "LaunchWorktreeRhino.bat"
  },
  "readiness": {
    "requiredFiles": [
      "LaunchWorktreeRhino.bat"
    ]
  }
}
```

## Install

Double-click `Install.bat`, or register a project during installation:

```powershell
pwsh -NoProfile -File src/RhinoWorktreeLauncher/Install-RhinoWorktreeLauncher.ps1 `
  -ProjectRoot C:\path\to\plugin-repository `
  -Launch
```

Each installation publishes a self-contained multifile release under `%LOCALAPPDATA%\RhinoWorktreeLauncher\releases\` and updates the Start Menu shortcut. The project catalog, cached launcher snapshot, and stable WebView2 profile remain under `%LOCALAPPDATA%\RhinoWorktreeLauncher\` across versioned releases.

## Add a project

Use **Add project** in the application and select a repository containing `.rhino-worktree-launcher.json`. Missing or invalid manifests are rejected rather than guessed.

## Build

```powershell
dotnet build src/RhinoWorktreeLauncher/RhinoWorktreeLauncher.csproj -c Debug
```

The executable is a thin .NET 8 WPF host around a local WebView2 application. Native code owns Git, GitHub CLI, dialogs, installation, and process launching. `src/RhinoWorktreeLauncher/Web/` owns the interface and communicates with the host through JSON messages.

Preview the web interface without the native host:

```powershell
python -m http.server 8765 --directory src/RhinoWorktreeLauncher
```

Open `http://localhost:8765/Web/index.html?theme=dark` or use `theme=light`. Add `sync=1` to preview the split LOCAL/GIT progress state.
