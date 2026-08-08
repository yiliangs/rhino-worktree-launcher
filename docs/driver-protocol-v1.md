# Repository driver protocol v1

Project identity, the driver mapping, Rhino launch settings, and the project driver live in RWL's application directory. No RWL configuration or driver file is required in a repository. RWL invokes one app-local driver for every worktree and supplies the selected checkout through `worktreePath` in the request.

## Invocation

RWL invokes the project's app-local driver noninteractively:

```powershell
pwsh -NoLogo -NoProfile -NonInteractive -ExecutionPolicy Bypass `
  -File "$env:LOCALAPPDATA\RhinoWorktreeLauncher\projects\<project-id>\Driver.ps1" `
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
{"protocolVersion":1,"kind":"result","success":true,"packageDirectory":"C:\\source\\plugin-task\\Plugin\\bin\\Debug\\net481","pluginPath":"C:\\source\\plugin-task\\Plugin\\bin\\Debug\\net481\\Plugin.rhp","rhinoRuntime":"netfx","criticalDependencies":[{"name":"Plugin.Core","path":"C:\\source\\plugin-task\\Plugin\\bin\\Debug\\net481\\Plugin.Core.dll"}],"receipt":{"launchIdEnvironmentVariable":"RWL_LAUNCH_ID","receiptPathEnvironmentVariable":"RWL_RECEIPT_PATH"},"registration":{"mode":"windows-registry-lease","pluginId":"f3cf4a28-ea9e-4e08-baba-5fc6645a5d72","startupCommand":"_MyPluginCommand"}}
```

A failed result sets `success` to `false` and supplies stable `errorCode` and human-readable `errorMessage`. A successful result must report existing absolute paths. The package directory, `.rhp`, and every critical dependency must be inside the selected worktree. `rhinoRuntime` is optional and, when supplied, is `netfx` or `netcore`; RWL maps it to Rhino's process runtime switch. `registration` is optional only for a plug-in GUID that is not already registered. When the same GUID is registered at another checkout, the driver must declare `windows-registry-lease`; `RHINO_PACKAGE_DIRS` can otherwise rewrite Rhino's persistent registration even when the selected-worktree receipt succeeds. RWL serializes starts for that GUID, preserves every existing HKLM/HKCU registration value and registry kind, redirects the paths, runs `startupCommand` to demand-load the selected plug-in, and restores the previous values after receipt verification.

## Loaded-binary receipt schema v1

RWL sets the two receipt environment variables on the Rhino child. Without `registration`, it also sets `RHINO_PACKAGE_DIRS`. With a registry lease, it uses the temporarily redirected registration and startup command instead. After the plug-in has loaded, repository-owned code writes the receipt atomically:

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
