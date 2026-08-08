# Rhino Worktree Launcher

Independent .NET 8 Windows application with one backend and three adapters: native WPF, `rwl` CLI, and stdio MCP.

## Architecture

- `RhinoWorktreeLauncher.Core` owns the schema-v4 app-local project catalog, stable Git repository identity, context resolution, worktree scanning, driver execution, process-scoped Rhino startup, receipt verification, diagnostics, and Claude configuration merging.
- `RhinoWorktreeLauncher` is the native WPF adapter. It binds backend DTOs and invokes backend commands in process. It must not run Git, project drivers, or Rhino directly.
- `Rwl.Cli` is the script/diagnostic adapter and Claude `SessionStart` hook target.
- `Rwl.Mcp` is a thin newline-delimited JSON-RPC stdio server over the same commands. Its launch tool remains one blocking request.
- `Rwl.Bootstrap` is the stable `%LOCALAPPDATA%\RhinoWorktreeLauncher\bootstrap\rwl.exe`. It resolves `current.json` and forwards to the current versioned desktop, CLI, or MCP executable.
- `templates/` and `docs/driver-protocol-v1.md` define the optional executable repository-integration surface.

The backend is a one-off bootstrapper, not a Rhino session monitor. Do not add durable launch operations, reattachment, background observation, or a service.

## Contract invariants

- Project settings belong only in `%LOCALAPPDATA%\RhinoWorktreeLauncher\projects.json`; never require or create repository configuration JSON.
- Catalog schema v4 stores project identity, Git common directory, primary checkout, app-local driver contract, and Rhino launch contract. Project drivers live under `%LOCALAPPDATA%\RhinoWorktreeLauncher\projects`; repositories contain no RWL driver or configuration file.
- Schema-v2 catalog data is imported once under the catalog lock and atomically replaced. Ordinary catalog reads never write or prune; invalid registrations remain visible as degraded until explicit removal or re-registration.
- Catalog writes re-read while holding the file lock and replace atomically.
- Optional GitHub/fetch failures are warnings and never hide local worktrees.
- Driver requests, events, terminal results, and receipts are versioned JSON.
- A successful driver result must identify selected-worktree artifacts. `rhinoRuntime` selects `/netfx` or `/netcore` when required.
- Rhino startup is brokered through the installed bootstrap opened by the interactive Windows shell. Per-launch environment overrides cross a current-user one-shot pipe; never mutate the launcher or shell environment. Drivers may use `RHINO_PACKAGE_DIRS` only for genuinely unregistered plug-in GUIDs; an already-registered same-GUID plug-in must declare a serialized Windows registry startup lease because Rhino can otherwise rewrite persistent registration despite a valid receipt. A lease must restore every previous registration value and registry kind before launch returns.
- Process creation is not success. Launch succeeds only after receipt launch ID, PID, `.rhp`, and every critical dependency path match. Timeout or mismatch terminates the unverified child.
- Every launch writes inert JSONL diagnostics under `%LOCALAPPDATA%\RhinoWorktreeLauncher\logs`.
- Claude install/remove owns only the `rhino-worktree-launcher` MCP entry and the RWL `session-context` hook. Preserve all unrelated settings and integrations.

## Build and verify

```powershell
dotnet build RhinoWorktreeLauncher.slnx -c Debug
dotnet test tests/RhinoWorktreeLauncher.Tests/RhinoWorktreeLauncher.Tests.csproj
```

The WPF design remains the completed fixed 720 × 1000 native surface. Preserve its theme, typography, scroll rail, shared corner-radius tokens, and current interaction details while changing backend behavior.
