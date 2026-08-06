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

Each installation publishes to a versioned folder under `%LOCALAPPDATA%\RhinoWorktreeLauncher\releases\` and updates the Start Menu shortcut. The project catalog remains at `%LOCALAPPDATA%\RhinoWorktreeLauncher\projects.json`.

## Add a project

Use **Add project** in the application and select a repository containing `.rhino-worktree-launcher.json`. Missing or invalid manifests are rejected rather than guessed.

## Build

```powershell
dotnet build src/RhinoWorktreeLauncher/RhinoWorktreeLauncher.csproj -c Debug
```
