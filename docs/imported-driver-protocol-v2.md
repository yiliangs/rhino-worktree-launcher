# Imported driver protocol v2

An imported driver is an advanced build escape hatch. RWL copies the user-selected PowerShell script into `%LOCALAPPDATA%\RhinoWorktreeLauncher\projects\<project-id>\drivers\Driver.ps1`. It never executes the original file and never places the copy in a repository.

RWL invokes the copy noninteractively with `-RequestPath <path>`. The request JSON has this shape:

```json
{
  "protocolVersion": 2,
  "command": "prepareBuild",
  "projectId": "example-plugin",
  "sourcePath": "C:\\...\\workspaces\\example-plugin\\<worktree-id>\\source",
  "buildPath": "C:\\...\\workspaces\\example-plugin\\<worktree-id>\\build"
}
```

`sourcePath` is the exact current RWL snapshot. `buildPath` is the persistent writable tree. The driver must build only in `buildPath` and emit exactly one terminal JSON object as a single stdout line:

```json
{
  "protocolVersion": 2,
  "kind": "result",
  "success": true,
  "pluginId": "f3cf4a28-ea9e-4e08-baba-5fc6645a5d72",
  "packageDirectory": "C:\\...\\build\\Plugin\\bin\\Debug\\net481",
  "pluginPath": "C:\\...\\build\\Plugin\\bin\\Debug\\net481\\Plugin.rhp",
  "rhinoRuntime": "netfx",
  "criticalDependencies": [
    { "name": "Plugin.Core", "path": "C:\\...\\build\\Plugin\\bin\\Debug\\net481\\Plugin.Core.dll" }
  ]
}
```

Every reported artifact must exist inside `buildPath`; the plug-in and critical dependencies must be inside `packageDirectory`. `rhinoRuntime` must be `netfx` or `netcore`. A failure result sets `success` to `false` and supplies `errorCode` and `errorMessage`.

Drivers may emit human-readable progress lines before the terminal result. The protocol does not include launch, registration, receipt, or repository paths because those responsibilities remain owned by RWL.
