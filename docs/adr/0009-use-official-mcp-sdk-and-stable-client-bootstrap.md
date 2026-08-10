# ADR 0009: Use the official MCP SDK and stable client bootstrap

Status: Accepted

## Context

RWL originally implemented JSON-RPC framing, MCP negotiation, cancellation, tool schemas, and tool dispatch by hand. Claude integration was a client-specific JSON editor, Codex had no setup path, and installation required a source checkout plus the .NET SDK. These shapes made protocol upgrades, client support, and end-user distribution separate maintenance problems.

## Decision

RWL uses the Tier 1 official C# MCP SDK over local stdio. The SDK owns protocol negotiation, framing, cancellation, schema generation, and tool metadata. `LauncherBackend` remains the only owner of project grants, worktree resolution, canonical solution execution, Rhino registration, launch, and binary verification.

The tool surface separates local inspection from external refresh:

- `rhino_worktree_list_worktrees` reads local Git state and cached metadata.
- `rhino_worktree_refresh_worktrees` explicitly contacts the configured remote and updates RWL-owned cache state.
- `rhino_worktree_build_and_launch` and `rhino_worktree_launch_existing` are marked destructive and long-running, report progress, and preserve backend verification as the terminal authority. Their separate names make the per-request build choice explicit.

All clients invoke `%LOCALAPPDATA%\RhinoWorktreeLauncher\bootstrap\rwl.exe mcp`. The bootstrap resolves the current versioned MCP executable from `current.json`, so application updates do not require client reconfiguration.

`McpClientIntegrationManager` is the single status/install/remove surface for Claude Code and Codex. It edits only RWL-owned configuration sections, preserves unrelated content, writes backups before changes, and configures a 300-second Codex tool timeout. Claude's optional SessionStart hook supplies exact-worktree context; it does not replace MCP instructions, tool schemas, annotations, or backend enforcement.

Tagged releases build a self-contained `win-x64` archive containing the desktop application, CLI, MCP server, bootstrap, verifier, installer, manifest, and SHA-256 checksum. The source-build installer remains available to developers.

## Consequences

- Protocol compatibility follows tested official SDK releases instead of repository-authored protocol code.
- Adding a client requires one adapter inside the shared integration manager plus status and preservation tests.
- Client configuration formats remain external compatibility surfaces and must be regression-tested when clients change them.
- Session context improves tool selection but is advisory. Correctness and safety stay in RWL code.
- Release CI, the stdio handshake test, the reproducible package build, and dependency updates form the maintenance loop.
- Binaries remain unsigned until Authenticode credentials and protected CI secrets are provisioned; SmartScreen warnings are therefore expected.
