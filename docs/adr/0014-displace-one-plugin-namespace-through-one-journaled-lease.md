# 0014: Displace one plug-in namespace through one journaled lease

## Status

Accepted. Amends ADR 0012 and ADR 0013, whose two separate displacement mechanisms become one. Amended 2026-08-17: the seed carries the load mode the displaced registration recorded, because a seed without one installs as a demand load. Amended 2026-08-18: no seed is written where a machine registration already names the selected artifact, because two registrations for one ID collide. Amended 2026-08-18, after that: both amendments were verified from a host whose current-user registry writes reach the registry, and the machine registration is corrected once more after Rhino exits, because Rhino writes the artifact it loaded back into it (ADR 0015).

## Context

ADR 0012 gave the launch a temporary current-user registration and ADR 0013 gave it a suspension of a competing machine registration. They were written months apart and landed as two components describing one idea: displace whatever is registered for a plug-in ID, run the launch, put it back exactly.

The two implementations diverged in the property that matters most. The machine suspension wrote a disk journal before removing anything and restored a pending journal on the next launch, so a killed launch could not permanently remove an installed registration. The current-user lease held its captured state in memory only.

That asymmetry was a defect, not a style difference. Under the ADR 0012 amendment the current-user lease writes Rhino's install seed, and a seed is not inert bookkeeping: it is a standing instruction. A launch killed between writing the seed and restoring leaves it in place, and the next ordinary Rhino session reads it, installs the worktree artifact permanently, and fills in a full registration for a file inside a Git worktree. The mechanism with the more dangerous residue had the weaker crash discipline.

The split cost correctness elsewhere too. Registration reading lived in a third component, so the launch read a key to decide there was a conflict and a different component re-read the same key to displace it, with a window between the two reads. That reading component also recognised only the installed shape, `PlugIn\FileName`, and was blind to a seed-form registration, which claims the same plug-in ID just as effectively.

The ADR 0012 amendment also left two current-user write shapes: seed a key that does not exist, redirect a key that does. Only the seed was ever verified live. The redirect branch, pointing `PlugIn\FileName` at the selected artifact and forcing `LoadMode`, is close to the hand-built complete registration the same live test showed Rhino silently ignoring, and it survived only because no test could distinguish it.

## Decision

One component owns everything registered for one (Rhino version, plug-in ID) pair in both hives, under one file lock and one journal file.

The journal is written before any registry mutation and holds both hives' pre-state. A null entry means the key did not exist, so restoring it deletes the key. That is what erases a killed launch's install seed. The journal is deleted only after a successful restore, and every launch restores a pending journal before reading any registration.

The current-user key is always cleared, and reseeded with the documented install seed, the root values `Name` and `FileName`, and, per the amendments below, the load mode the displaced registration recorded, except where a machine registration already names the selected artifact. The redirect branch is deleted. Every launch is a fresh install-load, and an existing current-user registration is displaced for the launch rather than edited in place, which also removes the unverified second shape.

A machine registration for the same ID that names a different file is displaced the same way where the user granted write access, and refuses the launch otherwise with the registered path and the exact key named, before Rhino starts. RWL still never elevates. A machine registration naming the selected artifact is not a competitor and is left alone.

A registration is recognised in either shape: `PlugIn\FileName` for an installed one, root `FileName` for a seed. Both claim the plug-in ID, so both are displaced or refused identically, in either hive.

Reading a registration and displacing it are one decision made once, inside the lease, so nothing re-reads a key another component just read. The launch coordinator holds one seam for the lease instead of one for scanning and one for suspending, and the lease reports what it displaced so the launch log can name it.

## Consequences

- A killed launch can no longer leave a live install seed behind. The worst residue is a registration missing until the next launch of the same plug-in, which the journal then restores.
- The current-user hive has one write shape, the one verified live on 2026-08-17. The unverified redirect is gone rather than kept as a fallback.
- A seed-form registration competing for the plug-in ID is now seen. Previously it was invisible to the conflict check and would have silently won the ID against the launch in the machine hive.
- An existing current-user registration is no longer preserved value by value during a launch. It is captured whole, removed, and recreated whole, so anything Rhino writes into the key while the seed is installed is discarded with the key rather than merged into the user's registration.
- Restoring the current-user hive precedes restoring the machine hive, so a machine restore that fails for access reasons still leaves no seed behind, and the journal survives for the next launch.
- Verification by file-use attribution (ADR 0002), refusal before Rhino starts, and the rule that the `.rhp` never appears on Rhino's command line are unchanged. This decision changes only how registrations are displaced and put back.

## Amendment (2026-08-17): the seed carries a recorded load mode

The seed of exactly `Name` and `FileName` had a consequence this ADR did not examine. Rhino derives a plug-in's load mode only by instantiating the plug-in, so a seed carries no answer and Rhino installs the plug-in as a demand load. A plug-in declaring `PlugInLoadTime.AtStartup` therefore does not load at startup under a launch. It loads when the user invokes one of its commands, which was observed against a plug-in whose console banner follows its own command invocation rather than Rhino's startup.

Rhino writes `LoadMode` only after the first real load, and this ADR's restore deletes the whole current-user key. The recorded value can therefore never persist across launches, so the state never corrects itself: every launch repeats the first-install state.

The consequence reached verification. A launch waits for the `.rhp` to map, which under a demand load cannot happen until the user invokes a command. An unattended Build and Launch runs to the timeout and terminates the Rhino child, so the launch reports failure for a plug-in that was registered correctly.

The seed therefore carries one more value: the load mode recorded by the registration the lease displaced. That value is Rhino's own cached answer for this plug-in ID rather than anything RWL derives, which keeps the lease free of any judgment about what a plug-in wants. Reading it costs nothing, because the journal already captures both hives' pre-state before any mutation.

Two rules make the carry well defined. The machine hive's value wins over the current-user one, matching how Rhino resolves a duplicate plug-in ID. A disabled mode is never carried, because the launch exists to load the selected artifact and verification waits on that load. With nothing displaced there is no recorded mode, and the seed stays exactly `Name` and `FileName`.

This shape is verified live. On 2026-08-17 a worktree launch of a plug-in declaring `PlugInLoadTime.AtStartup` loaded it at startup with no command typed, and verification completed 16 seconds after Rhino started. That launch suspended a machine registration naming a different file, so it exercised the seed path rather than resolving an existing complete registration. Rhino honours `LoadMode` in a seed and still installs the plug-in, so the extra value does not make Rhino read the key as already installed. That was the failure this amendment risked, and it would have loaded nothing at all, which is worse than the demand load being replaced.

The ADR 0012 amendment remains accurate for the shape it tested. A hand-built complete registration carrying `LoadMode` is still ignored, and that registration also lacked `Type`, `CommandList`, `RegPath`, and `DirectoryInstall`, so it never isolated `LoadMode`. A seed carrying a load mode and a hand-built complete registration are different shapes, and only the first one loads.

## Amendment (2026-08-18): no seed beside a machine registration naming the selected artifact

This ADR treats a machine registration that names the selected artifact as not a competitor and leaves it in place, which is correct. It then seeded the current-user hive anyway, which is not.

That combination gives Rhino two registrations for one plug-in ID: a complete machine registration holding `LoadMode` and `PlugIn\FileName`, and a bare current-user seed naming the same file. A seed is a request to install an ID, and ADR 0012 already recorded Rhino rejecting an ID already in use when a launch offered one plug-in through two mechanisms at once. The same collision appears here across the two hives, and the plug-in does not load.

The case is common rather than exotic. It is every launch of the checkout the plug-in is normally installed from, so the launch that most resembles ordinary use was the one that failed.

The launch therefore writes no current-user seed where the machine registration already names the selected artifact. Nothing is needed: Rhino holds a complete registration for exactly the file the launch wants loaded, including the load mode it derived when it last loaded it. The current-user key is still cleared under the same journal, because a current-user registration naming a different file would otherwise win the ID, and clearing it is what the restore later undoes.

This also bounds the previous amendment. A carried load mode matters only where the launch writes a seed, which is now only where no surviving registration already names the selected artifact.

The collision is inferred from two observed states rather than isolated by a controlled test: a Start menu session resolving the machine registration alone loads the plug-in at startup, and an RWL launch of the same artifact with a seed added does not load it. One live launch of the primary checkout confirms the fix.

## Amendment (2026-08-18): what the verifications above assumed, and what Rhino writes back

Both amendments above rest on live launches: the 2026-08-17 launch that loaded a plug-in declaring `PlugInLoadTime.AtStartup` 16 seconds after Rhino started, and the launch of the primary checkout that confirmed writing no seed beside a machine registration naming the selected artifact. Both ran from a process whose current-user registry writes reached the registry. Both remain accurate, and neither generalises past that condition, which nobody knew was a condition at the time.

Later on 2026-08-18 a series of launches failed while executing this ADR's mechanism correctly, from a host whose current-user writes were intercepted and never arrived. RWL's MCP server, spawned by its client, cleared the current-user key and wrote exactly the seed decided here, carrying the displaced registration's load mode where there was one. It read the key back and saw what it had written, while an external sampler polling every 100 ms never observed the key exist. The machine hive passed through in the same process. The same release's CLI, from an ordinary shell, seeded and loaded the worktree plug-in in 15 to 24 seconds, which is this ADR's mechanism working. The seed design was never the defect: the lease computed the right shape and issued the right writes, the writes did not land, and no reader inside that process could have told. ADR 0015 records the response, and it changes only which process performs the write and what must be proven before Rhino starts, not what this ADR decides to write or to leave alone.

The same session found one thing this ADR's restore does not survive. Rhino runs elevated on that machine, and after loading a plug-in it writes the artifact's path back into the machine registration. The restore here runs at verification, while the Rhino it started is still alive, so a write-back lands after the restore and the machine registration ends the session naming a worktree artifact, which the user's next ordinary Rhino start then loads. That is a stale worktree build loaded outside any launch, from a hive this ADR had already put back. ADR 0015 keeps the journal alive past the launch's own restore: the executor lingers detached until Rhino exits, compares both hives against the journaled pre-state, restores again if either drifted, and only then deletes the journal. That comparison also required the machine hive's pre-state to be captured whether or not the launch displaces it, so an absent key can be told from an untouched one.
