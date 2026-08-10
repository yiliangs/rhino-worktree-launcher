# 0010: Build and load canonical solution artifacts

## Status

Accepted

## Context

RWL previously copied working-tree source into an application-owned workspace, built a separate artifact tree, and optionally ran an imported driver. This produced two plausible `.rhp` files: the one Visual Studio and the repository considered canonical, and the one RWL privately built. It also made build freshness depend on which tool performed the build. A receipt written only by RWL could not recognize a valid Visual Studio build, while timestamp inference could not reliably model arbitrary MSBuild inputs.

## Decision

The selected Git worktree is the single source and build tree. Each registered project stores one canonical Rhino plug-in project, Visual Studio solution, solution Configuration and Platform, plus a desktop launch-mode default.

Build & Launch runs `dotnet build` on that solution in the selected worktree. Direct Launch performs no build and makes no freshness claim. In both modes, RWL reopens the solution, verifies that it contains the canonical Rhino plug-in project and selected configuration, resolves the project's mapped configuration, and asks MSBuild for its `TargetPath`. RWL registers and verifies only that exact `.rhp` and its declared critical dependencies.

Launch mode belongs to the invoking adapter. The desktop uses the project-specific default from Config. The MCP server instead publishes separate Build & Launch and Launch Existing tools so an agent chooses explicitly for each request. MCP launch tools do not inherit the desktop default. Both remain inside the same RWL launch and loaded-binary verification path.

If a repository contains more than one Rhino plug-in project, Config presents every candidate and requires an explicit canonical choice. It identifies projects from their source and project metadata, never by choosing among existing `.rhp` outputs. If more than one solution contains the selected plug-in project, the user must also choose a solution explicitly. RWL does not fall back to building a project file. The imported-driver mode and application-owned source/build workspaces are removed.

## Consequences

- Visual Studio and RWL share one set of ordinary build outputs and one source-of-truth `.rhp`.
- Repository-owned solution settings, imports, targets, and output paths remain authoritative.
- Build & Launch may modify ordinary build outputs in the selected worktree.
- Direct Launch is fast but may load a stale artifact; the UI and MCP tool description state that it does not rebuild or claim freshness.
- RWL no longer claims repository write isolation for builds.
- Custom build orchestration must be expressed by the selected solution and MSBuild configuration.
- The app-owned catalog still stores the selection, grants, and desktop launch-mode default; remote mirrors, launch verification state, and logs remain app-owned.

## Supersedes

ADR 0001, ADR 0005, ADR 0006, ADR 0007, and ADR 0008.
