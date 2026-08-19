# 0012: Load the selected plug-in through one complete registration

## Status

Accepted. Amended 2026-08-17: the overlay's registration shape is the documented install seed or a redirect of an existing registration, not a hand-built complete registration (see Amendment). Amended 2026-08-18: every live-test conclusion recorded here holds only for a host whose current-user registry writes reach the registry, which not every launcher host does (see the second Amendment and ADR 0015). Amended by ADR 0013, which suspends a competing machine registration where the user granted write access. Amended by ADR 0014, which makes the install seed the only current-user shape, deletes the redirect branch, and moves capture and restore onto a disk journal.

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

## Amendment (2026-08-17)

A live test falsified the original registration shape. A hand-built complete registration, holding `Name`, `EnglishName`, `LoadMode`, and `IsDotNETPlugIn` beside `PlugIn\FileName`, was silently ignored: Rhino started cleanly with no competing registration present and never loaded the file.

Rhino loads a plug-in it has never seen only through the documented install seed: a `Plug-ins\{id}` key holding exactly the root values `Name` and `FileName`. At its next startup Rhino installs that plug-in, loads the file, and fills in the full registration itself.

The lease therefore writes one of two shapes. For a plug-in with no current-user registration it writes the install seed and nothing else. For a plug-in whose current-user registration already exists it redirects that registration, pointing `PlugIn\FileName` at the selected artifact and forcing `LoadMode` to a startup load. The capture-and-restore discipline is unchanged: a key the lease created is removed wholesale, which also removes everything Rhino filled in during the install.

ADR 0014 later removed the redirect branch, which was never verified live, and made the install seed the only shape: an existing current-user registration is captured whole, removed, and reseeded.

## Amendment (2026-08-18): the live tests above hold only where the write reached the registry

The two conclusions this ADR draws from live tests, that a hand-built complete registration is silently ignored and that Rhino loads a plug-in it has never seen only through the documented install seed, were reached on 2026-08-17 from a process whose current-user registry writes reached the registry. They stay accurate for that condition, which is the only condition they were ever observed in, and this ADR never stated it because no one knew it could fail.

On 2026-08-18 a launcher host was found that does not meet it. RWL's MCP server, spawned by its client, ran with its current-user registry writes intercepted. It wrote the install seed, read the key back, and saw exactly what it had written, while an external sampler polling every 100 ms never observed that key exist, across several launches. The machine hive passed through in the same process, so its HKLM reads and writes were real. The same release's CLI, started from an ordinary shell, wrote a seed that an external reader could see and loaded the worktree plug-in in 15 to 24 seconds. The process ran as the same user at high integrity, with no AppContainer, no UAC virtualisation and no restricted token, and the shipped binary decompiled identically to its source. The component performing the interception was not identified.

Nothing above is falsified by that, because a registration shape can only be tested where the write lands, and each of these was tested where it did. The finding bounds the conclusions instead. A launch from an intercepted host says nothing about registration shapes at all: Rhino resolves the plug-in from a hive the seed never reached, so every shape fails there identically, and the writing process cannot tell the difference by reading its own key.

ADR 0015 is the response. Every registry mutation moves into a launch executor that the interactive Windows shell starts, which is outside the interception, and an independently spawned reader must confirm the seed before Rhino starts, so a write that never landed ends the launch with `registry_seed_not_visible` instead of producing a registration this ADR's mechanism cannot make Rhino load.
