# 0013: Suspend a competing machine registration through granted access

## Status

Accepted. Amends ADR 0012, whose refusal of a competing machine registration becomes the fallback rather than the only behavior.

## Context

ADR 0012 made the temporary current-user registration the only plug-in loading mechanism and established, through a live test, that a machine-wide registration for the same plug-in ID always wins over it: HKCU does not shadow HKLM for a duplicate plug-in ID. ADR 0012 therefore refused such launches and asked the user to remove the machine registration.

Permanent removal is the wrong general remedy. On a machine where the plug-in is genuinely installed for all users, removing the machine registration breaks the plug-in for every ordinary Rhino session just to allow worktree launches. Rewriting the machine registration in place would reintroduce a second loading mechanism, the exact defect ADR 0012 removed. Per-launch elevation through a UAC prompt is unacceptable because the MCP adapter launches headlessly and cannot answer one.

## Decision

Where the launching account holds write access to the machine `Plug-ins` key, granted once by the user with an elevated account, a launch that finds a competing machine registration suspends it instead of refusing: the registration's key tree is captured to a journal file on disk, the key is removed, the launch proceeds through the unchanged current-user lease, and the registration is restored from the journal when the launch ends. Restore is delete-then-recreate from the journal, so a partial state can never merge with stale remains.

The journal survives a crash. Every launch of the same plug-in restores a pending journal before scanning the registry, so a machine registration lost to a crashed launch reappears on the next launch attempt.

Without granted write access the behavior is ADR 0012's refusal, and the message now states both remedies: grant write access so launches can suspend and restore the registration, or remove the key if it is stale.

RWL still never elevates and never runs elevated. The current-user lease remains the only loading mechanism; the suspension only removes the competitor for the duration of the launch.

## Consequences

- Worktree launches work on machines carrying an all-users install of the same plug-in, and ordinary Rhino sessions keep loading the installed copy outside launch windows.
- The user opts into a wider security surface: with write access granted on the machine `Plug-ins` key, any process running as that account can rewrite machine-wide plug-in registrations. The grant is per user and per Rhino version key, and refusing it keeps the ADR 0012 behavior.
- A crash between removal and restore leaves the machine registration absent until the next launch of that plug-in restores the journal. A normal Rhino session started in that window does not load the installed plug-in.
- Concurrent launches of the same plug-in serialize on a suspension lock file beside the existing registration lease lock.
