# Repository driver protocol v1

Each registered repository commits `.rhino-worktree-launcher.json` at its root:

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

Registration is the trust decision. RWL may execute the selected worktree's copy of the declared driver and any build scripts it invokes.

## Invocation

RWL invokes the selected worktree's driver noninteractively:

```powershell
pwsh -NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass `
  -File tools/rhino-worktree/Driver.ps1 `
  -RequestPath <absolute-request-json>
```

The request is a versioned JSON object:

```json
{
  "protocolVersion": 1,
  "command": "prepareLaunch",
  "launchId": "4a41...",
  "worktreePath": "C:\\source\\plugin-task",
  "receiptPath": "C:\\Temp\\RhinoWorktreeLauncher\\4a41...\\launch-receipt.json"
}
```

The driver writes newline-delimited JSON to stdout. It may write progress events before exactly one terminal result:

```json
{"protocolVersion":1,"kind":"event","stage":"build","message":"Building Debug x64."}
{"protocolVersion":1,"kind":"result","success":true,"packageDirectory":"C:\\source\\plugin-task\\Plugin\\bin\\Debug\\net481","pluginPath":"C:\\source\\plugin-task\\Plugin\\bin\\Debug\\net481\\Plugin.rhp","rhinoRuntime":"netfx","criticalDependencies":[{"name":"Plugin.Core","path":"C:\\source\\plugin-task\\Plugin\\bin\\Debug\\net481\\Plugin.Core.dll"}],"receipt":{"launchIdEnvironmentVariable":"RWL_LAUNCH_ID","receiptPathEnvironmentVariable":"RWL_RECEIPT_PATH"}}
```

A failed result sets `success` to `false` and supplies stable `errorCode` and human-readable `errorMessage`. A successful result must report existing absolute paths. The package directory, `.rhp`, and every critical dependency must be inside the selected worktree. `rhinoRuntime` is optional and, when supplied, is `netfx` or `netcore`; RWL maps it to Rhino's process runtime switch.

## Loaded-binary receipt schema v1

RWL starts Rhino with `RHINO_PACKAGE_DIRS` set only on that child process. It also sets the two environment variables declared by the driver result. After the plug-in has loaded, repository-owned code writes the receipt atomically:

```json
{
  "schemaVersion": 1,
  "status": "loaded",
  "launchId": "4a41...",
  "processId": 12345,
  "pluginPath": "C:\\source\\plugin-task\\Plugin\\bin\\Debug\\net481\\Plugin.rhp",
  "criticalDependencies": [
    {
      "name": "Plugin.Core",
      "path": "C:\\source\\plugin-task\\Plugin\\bin\\Debug\\net481\\Plugin.Core.dll"
    }
  ]
}
```

RWL fails closed unless the schema, launch ID, Rhino process ID, `.rhp` path, and every driver-declared dependency path match. The receipt is the source of truth. Process startup alone is not success.

A plug-in that catches a required load failure may write the same receipt with `status` set to `failed` and an `error` string. RWL treats that receipt as a terminal verification failure.

Copy [`WorktreeLaunchReceiptBootstrap.cs`](../templates/WorktreeLaunchReceiptBootstrap.cs) into a plug-in and call `WriteLoadedReceipt` after its required assemblies have loaded. The template uses only .NET Framework BCL APIs.
