# Rhino Worktree Launcher

Independent .NET 8 Windows application with one backend and three adapters: native WPF, `rwl` CLI, and stdio MCP.

## Architecture

- `RhinoWorktreeLauncher.Core` owns the schema-v6 app-local project catalog, stable Git worktree identity, read-only repository scanning, canonical solution discovery and builds, remote mirrors, process-scoped Rhino startup, exact loaded-binary verification, diagnostics, and client configuration merging.
- `RhinoWorktreeLauncher` is the native WPF adapter. It binds backend DTOs and invokes backend commands in process. It must not run Git, MSBuild, or Rhino directly.
- `Rwl.Cli` is the script/diagnostic adapter and Claude `SessionStart` hook target.
- `Rwl.Mcp` is a thin newline-delimited JSON-RPC stdio server over the same commands. Its launch tool remains one blocking request.
- `Rwl.Bootstrap` is the stable `%LOCALAPPDATA%\RhinoWorktreeLauncher\bootstrap\rwl.exe`. It resolves `current.json` and forwards to the current versioned desktop, CLI, or MCP executable.
- `eng/New-RwlPackage.ps1` is the only installable-payload producer. Source installs, packaged installs, and releases consume its payload shape; the installer never owns a second build or publish implementation.

The backend is a one-off bootstrapper, not a Rhino session monitor. Do not add durable launch operations, reattachment, background observation, or a service.

## Contract invariants

- Project settings belong only in `%LOCALAPPDATA%\RhinoWorktreeLauncher\projects.json`; never require or create repository configuration JSON.
- Catalog schema v6 stores project identity, Git common directory, primary checkout, explicit grants, Rhino version, canonical plug-in project, solution Configuration and Platform, and the desktop launch-mode default. Repositories contain no RWL driver or configuration file.
- Schema-v2 catalog data is imported once under the catalog lock and atomically replaced. Ordinary catalog reads never write or prune; invalid registrations remain visible as degraded until explicit removal or re-registration.
- A saved build profile is checked against the primary checkout on every catalog read, cheaply: two existence checks on the saved solution and plug-in project. Only when those fail does the read scan for any remaining Rhino plug-in project. One remaining keeps the project available with a warning to choose it again in Config; none remaining degrades the project, because Config has nothing left to offer. Registration already refuses a repository with no Rhino plug-in project, so the two gates agree.
- Catalog writes re-read while holding the file lock and replace atomically.
- Remote failures are warnings and never hide local worktrees. Remote reads use an RWL-owned mirror and never fetch into a registered repository.
- The selected Git worktree is the single source of truth. Do not create an app-owned source or build tree.
- Config requires an explicit canonical plug-in project when a repository has more than one candidate; discovery must remain available so the UI can present that choice. Never infer project identity from existing `.rhp` outputs.
- Build & Launch runs the selected solution and its Configuration and Platform in the selected worktree. Direct Launch skips the build. Both resolve the selected plug-in project's mapped MSBuild `TargetPath`; never guess, copy, or select an `.rhp` by filename.
- Launch mode belongs to the invoking adapter. The desktop and CLI pass the saved Config default; MCP publishes separate Build & Launch and Launch Existing tools and never inherits the desktop default.
- Rhino startup uses a serialized temporary HKCU registration as its only plug-in loading mechanism (ADR 0012, 0014). The lease always clears the current-user key and writes the documented install seed, exactly root `Name` and `FileName`, which Rhino installs and loads at startup; a hand-built complete registration is silently ignored, and there is no redirect shape. Never also pass the `.rhp` on Rhino's command line: that asks Rhino to install an ID the overlay has already registered, and Rhino rejects it as an ID already in use. Restore before launch returns; never require elevation.
- One lease owns both hives for one (Rhino version, plug-in ID) pair under one lock and one journal (ADR 0014). The journal holds both hives' pre-state and is written before any mutation; a null entry means the key did not exist, so restoring deletes it, which is what erases a killed launch's install seed. Every launch restores a pending journal before reading any registration. Reading a registration and displacing it are one decision made once: never re-read a key another component just read for the same decision. A registration counts in either shape, `PlugIn\FileName` or seed-form root `FileName`, in either hive.
- A machine-wide registration for the selected plug-in ID that names a different file always wins over the current-user seed (verified live: HKCU does not shadow HKLM for a duplicate plug-in ID). Where the user granted write access to the machine `Plug-ins` key, the lease displaces that registration and restores it when the launch ends (ADR 0013). Without granted access, launch refuses before Rhino starts and names the registered path, the key, and both remedies; RWL never elevates. A machine registration naming the selected artifact is not a competitor. An existing current-user registration never blocks a launch: it is captured whole, displaced, and restored.
- Process creation is not success. Launch succeeds only after the launched Rhino process holds the selected `.rhp` mapped in its address space (external file-use attribution, ADR 0002 — no in-Rhino verifier plug-in, which can never verify its own loading). Critical dependencies are existence-checked beside the plug-in during prepare, not load-gated: lazily-loaded dependencies are legitimately unmapped at startup. Timeout terminates the unverified child.
- Every launch writes inert JSONL diagnostics under `%LOCALAPPDATA%\RhinoWorktreeLauncher\logs`.
- Verification builds may compile the solution, but only the canonical package producer defines distributable binaries.
- Claude install/remove owns only the `rhino-worktree-launcher` MCP entry and the RWL `session-context` hook. Preserve all unrelated settings and integrations.

## Build and verify

```powershell
dotnet build RhinoWorktreeLauncher.slnx -c Debug
dotnet test tests/RhinoWorktreeLauncher.Tests/RhinoWorktreeLauncher.Tests.csproj
dotnet test tests/RhinoWorktreeLauncher.UiTests/RhinoWorktreeLauncher.UiTests.csproj
```

`UiTests` is the seam for the WPF surface: it asserts against the XAML documents and the built desktop output, so any change to the native surface is verified there rather than by eye alone.

The WPF design remains the completed fixed 720 x 1000 native surface. Preserve its theme, typography, scroll rail, shared corner-radius tokens, and current interaction details while changing backend behavior. `Config` is project-specific; global `Settings` owns MCP setup.
