# Rhino Worktree Launcher

Independent .NET 8 Windows application with one backend and three adapters: native WPF, `rwl` CLI, and stdio MCP.

## Architecture

- `RhinoWorktreeLauncher.Core` owns the schema-v5 app-local project catalog, stable Git worktree identity, read-only repository scanning, app-owned workspaces and remote mirrors, typed builds or imported-driver execution, process-scoped Rhino startup, exact loaded-binary verification, diagnostics, and Claude configuration merging.
- `RhinoWorktreeLauncher` is the native WPF adapter. It binds backend DTOs and invokes backend commands in process. It must not run Git, project drivers, or Rhino directly.
- `Rwl.Cli` is the script/diagnostic adapter and Claude `SessionStart` hook target.
- `Rwl.Mcp` is a thin newline-delimited JSON-RPC stdio server over the same commands. Its launch tool remains one blocking request.
- `Rwl.Bootstrap` is the stable `%LOCALAPPDATA%\RhinoWorktreeLauncher\bootstrap\rwl.exe`. It resolves `current.json` and forwards to the current versioned desktop, CLI, or MCP executable.
- `Rwl.RhinoVerifier` is the bundled Rhino 8 verifier. `docs/imported-driver-protocol-v2.md` defines the optional custom-build escape hatch.

The backend is a one-off bootstrapper, not a Rhino session monitor. Do not add durable launch operations, reattachment, background observation, or a service.

## Contract invariants

- Project settings belong only in `%LOCALAPPDATA%\RhinoWorktreeLauncher\projects.json`; never require or create repository configuration JSON.
- Catalog schema v5 stores project identity, Git common directory, primary checkout, explicit read grants, Rhino version, and the app-owned build profile. Optional imported drivers live under `%LOCALAPPDATA%\RhinoWorktreeLauncher\projects`; repositories contain no RWL driver or configuration file.
- Schema-v2 catalog data is imported once under the catalog lock and atomically replaced. Ordinary catalog reads never write or prune; invalid registrations remain visible as degraded until explicit removal or re-registration.
- Catalog writes re-read while holding the file lock and replace atomically.
- Remote failures are warnings and never hide local worktrees. Remote reads use an RWL-owned mirror and never fetch into a registered repository.
- Snapshot tracked files plus nonignored untracked files into an app-owned persistent worktree. Restore, build, package caches, temporary files, and verification state must remain there.
- Typed build profiles are the default. Imported-driver protocol v2 is versioned JSON, runs only from the copied app-owned script, and may report artifacts only from the RWL build tree.
- Rhino startup uses a serialized temporary HKCU plug-in path overlay and the app-owned verifier. Restore the exact previous current-user value before launch returns; never require elevation or modify HKLM.
- Process creation is not success. Launch succeeds only after the verifier's launch ID, PID, `.rhp`, and every critical dependency path match. Timeout or mismatch terminates the unverified child.
- Every launch writes inert JSONL diagnostics under `%LOCALAPPDATA%\RhinoWorktreeLauncher\logs`.
- Claude install/remove owns only the `rhino-worktree-launcher` MCP entry and the RWL `session-context` hook. Preserve all unrelated settings and integrations.

## Build and verify

```powershell
dotnet build RhinoWorktreeLauncher.slnx -c Debug
dotnet test tests/RhinoWorktreeLauncher.Tests/RhinoWorktreeLauncher.Tests.csproj
```

The WPF design remains the completed fixed 720 × 1000 native surface. Preserve its theme, typography, scroll rail, shared corner-radius tokens, and current interaction details while changing backend behavior.
