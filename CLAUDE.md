# Rhino Worktree Launcher

Independent .NET 8 Windows application with one backend and three adapters: native WPF, `rwl` CLI, and stdio MCP.

## Architecture

- `RhinoWorktreeLauncher.Core` owns the schema-v6 app-local project catalog, stable Git worktree identity, read-only repository scanning, canonical solution discovery and builds, remote mirrors, process-scoped Rhino startup, exact loaded-binary verification, diagnostics, and client configuration merging.
- `RhinoWorktreeLauncher` is the native WPF adapter. It binds backend DTOs and invokes backend commands in process. It must not run Git, MSBuild, or Rhino directly.
- `Rwl.Cli` is the script/diagnostic adapter and Claude `SessionStart` hook target.
- `Rwl.Mcp` is a thin newline-delimited JSON-RPC stdio server over the same commands. Its launch tool remains one blocking request.
- `Rwl.Bootstrap` is the stable `%LOCALAPPDATA%\RhinoWorktreeLauncher\bootstrap\rwl.exe`. It resolves `current.json` and forwards to the current versioned desktop, CLI, or MCP executable.
- `Rwl.RhinoVerifier` is the bundled Rhino 8 verifier.
- `eng/New-RwlPackage.ps1` is the only installable-payload producer. Source installs, packaged installs, and releases consume its payload shape; the installer never owns a second build or publish implementation.

The backend is a one-off bootstrapper, not a Rhino session monitor. Do not add durable launch operations, reattachment, background observation, or a service.

## Contract invariants

- Project settings belong only in `%LOCALAPPDATA%\RhinoWorktreeLauncher\projects.json`; never require or create repository configuration JSON.
- Catalog schema v6 stores project identity, Git common directory, primary checkout, explicit grants, Rhino version, canonical plug-in project, solution Configuration and Platform, and the desktop launch-mode default. Repositories contain no RWL driver or configuration file.
- Schema-v2 catalog data is imported once under the catalog lock and atomically replaced. Ordinary catalog reads never write or prune; invalid registrations remain visible as degraded until explicit removal or re-registration.
- Catalog writes re-read while holding the file lock and replace atomically.
- Remote failures are warnings and never hide local worktrees. Remote reads use an RWL-owned mirror and never fetch into a registered repository.
- The selected Git worktree is the single source of truth. Do not create an app-owned source or build tree.
- Config requires an explicit canonical plug-in project when a repository has more than one candidate; discovery must remain available so the UI can present that choice. Never infer project identity from existing `.rhp` outputs.
- Build & Launch runs the selected solution and its Configuration and Platform in the selected worktree. Direct Launch skips the build. Both resolve the selected plug-in project's mapped MSBuild `TargetPath`; never guess, copy, or select an `.rhp` by filename.
- Launch mode belongs to the invoking adapter. The desktop and CLI pass the saved Config default; MCP publishes separate Build & Launch and Launch Existing tools and never inherits the desktop default.
- Rhino startup uses a serialized temporary HKCU plug-in path overlay and the app-owned verifier. Restore the exact previous current-user value before launch returns; never require elevation or modify HKLM.
- Process creation is not success. Launch succeeds only after the verifier's launch ID, PID, `.rhp`, and every critical dependency path match. Timeout or mismatch terminates the unverified child.
- Every launch writes inert JSONL diagnostics under `%LOCALAPPDATA%\RhinoWorktreeLauncher\logs`.
- Verification builds may compile the solution, but only the canonical package producer defines distributable binaries.
- Claude install/remove owns only the `rhino-worktree-launcher` MCP entry and the RWL `session-context` hook. Preserve all unrelated settings and integrations.

## Build and verify

```powershell
dotnet build RhinoWorktreeLauncher.slnx -c Debug
dotnet test tests/RhinoWorktreeLauncher.Tests/RhinoWorktreeLauncher.Tests.csproj
```

The WPF design remains the completed fixed 720 x 1000 native surface. Preserve its theme, typography, scroll rail, shared corner-radius tokens, and current interaction details while changing backend behavior. `Config` is project-specific; global `Settings` owns MCP setup.
