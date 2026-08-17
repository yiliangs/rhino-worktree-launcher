# 0012: Load the selected plug-in through one complete registration

## Status

Accepted

## Context

Launch used two loading mechanisms at once. It wrote a temporary current-user overlay setting `Plug-ins\{id}\PlugIn\FileName` to the selected worktree artifact, and it also passed that `.rhp` on Rhino's command line, on the stated ground that the overlay alone could not make Rhino load a plug-in it had never seen.

The two mechanisms compete for one plug-in ID. Rhino resolves a plug-in by ID, so the overlay makes the ID registered, and Rhino then rejects the same file offered on its command line with "ID already in use". The overlay could not load it either: a key holding only `FileName`, without `LoadMode`, is registered but not a startup load. Every launch therefore produced a registered plug-in that never loaded, an error dialog naming the selected artifact, and a verification wait that ran until Rhino exited.

Rhino resolves plug-in registrations from both HKLM and HKCU. A machine-wide registration for the same ID, which is the normal state on a developer machine after an install or after debugging another checkout, names a different file and competes for the same ID. Schemes do not help: a `Scheme:` key carries settings only and has no per-scheme plug-in path, so RWL cannot obtain a private plug-in namespace.

A live test on 2026-08-17 settled the precedence question. With a complete current-user startup registration for the selected worktree artifact and a machine-wide registration for the same ID naming another file, Rhino 8 resolved the ID to the machine-wide file. HKCU does not shadow HKLM for a duplicate plug-in ID, so no current-user mechanism can win that ID, and a launch attempted against a competing machine registration can only run to the verification timeout.

## Decision

The temporary current-user registration is the only loading mechanism. It writes the complete registration Rhino needs to load the selected artifact at startup, including `Name`, `LoadMode`, and `IsDotNETPlugIn` beside `PlugIn\FileName`, and the launch never passes the `.rhp` on Rhino's command line.

The lease captures and restores every value it writes. A key the launch created is removed wholesale, and a key that already existed keeps everything the launch did not write.

Launch reads the registrations for the selected plug-in ID in both hives before applying the overlay. A machine-wide registration that names a different file makes the launch unwinnable, so launch refuses during prepare, before Rhino starts, and states the exact registry key to remove. RWL never rewrites that key, because modifying HKLM requires elevation. A current-user registration that names a different file is only a warning: the lease overlays it for the launch and restores it afterward.

## Consequences

- The selected worktree artifact is the only file registered for its ID during a launch, so verification tests the intended binary.
- A competing machine registration remains possible, and the user resolves it by removing that registration with an elevated account. RWL refuses fast and states the exact key instead of timing out without explanation.
- The lease restores a larger set of values than a single file name, and Rhino's own writes into a pre-existing key survive the launch.
- Verification by file-use attribution (ADR 0002) is unchanged. This decision changes only how the selected plug-in is offered to Rhino.
