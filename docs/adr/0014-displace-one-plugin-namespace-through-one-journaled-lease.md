# 0014: Displace one plug-in namespace through one journaled lease

## Status

Accepted. Amends ADR 0012 and ADR 0013, whose two separate displacement mechanisms become one. Amended 2026-08-17: the seed carries the load mode the displaced registration recorded, because a seed without one installs as a demand load (see Amendment).

## Context

ADR 0012 gave the launch a temporary current-user registration and ADR 0013 gave it a suspension of a competing machine registration. They were written months apart and landed as two components describing one idea: displace whatever is registered for a plug-in ID, run the launch, put it back exactly.

The two implementations diverged in the property that matters most. The machine suspension wrote a disk journal before removing anything and restored a pending journal on the next launch, so a killed launch could not permanently remove an installed registration. The current-user lease held its captured state in memory only.

That asymmetry was a defect, not a style difference. Under the ADR 0012 amendment the current-user lease writes Rhino's install seed, and a seed is not inert bookkeeping: it is a standing instruction. A launch killed between writing the seed and restoring leaves it in place, and the next ordinary Rhino session reads it, installs the worktree artifact permanently, and fills in a full registration for a file inside a Git worktree. The mechanism with the more dangerous residue had the weaker crash discipline.

The split cost correctness elsewhere too. Registration reading lived in a third component, so the launch read a key to decide there was a conflict and a different component re-read the same key to displace it, with a window between the two reads. That reading component also recognised only the installed shape, `PlugIn\FileName`, and was blind to a seed-form registration, which claims the same plug-in ID just as effectively.

The ADR 0012 amendment also left two current-user write shapes: seed a key that does not exist, redirect a key that does. Only the seed was ever verified live. The redirect branch, pointing `PlugIn\FileName` at the selected artifact and forcing `LoadMode`, is close to the hand-built complete registration the same live test showed Rhino silently ignoring, and it survived only because no test could distinguish it.

## Decision

One component owns everything registered for one (Rhino version, plug-in ID) pair in both hives, under one file lock and one journal file.

The journal is written before any registry mutation and holds both hives' pre-state. A null entry means the key did not exist, so restoring it deletes the key. That is what erases a killed launch's install seed. The journal is deleted only after a successful restore, and every launch restores a pending journal before reading any registration.

The current-user key is always cleared and reseeded with the documented install seed, the root values `Name` and `FileName`, and, per the amendment below, the load mode the displaced registration recorded. The redirect branch is deleted. Every launch is a fresh install-load, and an existing current-user registration is displaced for the launch rather than edited in place, which also removes the unverified second shape.

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

This shape is not verified live. The ADR 0012 amendment records that a hand-built complete registration including `LoadMode` was silently ignored, which does not isolate `LoadMode` (that registration also lacked `Type`, `CommandList`, `RegPath`, and `DirectoryInstall`) but is the nearest evidence and does not favour this change. The failure mode to watch for is Rhino reading the key as already installed and skipping the install, which loads nothing at all and is worse than the demand load it replaces. Retire this amendment and restore the two-value seed if one live launch shows that.
