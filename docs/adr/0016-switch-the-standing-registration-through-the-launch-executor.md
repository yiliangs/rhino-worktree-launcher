# 0016: Switch the standing registration through the launch executor

## Status

Accepted. Extends ADR 0015, whose rule that no registry mutation may run in a launcher host
process now covers a mutation that is not part of a launch. It leaves ADR 0012, ADR 0013 and
ADR 0014 unchanged: the temporary lease remains the only mechanism by which a launch loads a
plug-in, and nothing here displaces, journals, or restores anything.

## Context

RWL had no name for the registration Rhino resolves for a plug-in ID when no launch is
running, and no way to see or change it. The desktop showed a chip reading DEFAULT bound to
the catalog's `primaryCheckout`, which is a Git fact about the repository's main working tree
and says nothing about which build Rhino loads. On the machine this was found, the chip sat
on `master` while the machine hive registered a `.rhp` from a worktree under
`.claude/worktrees`, so every Rhino started outside RWL loaded that worktree's build and the
surface asserted the opposite.

Changing it was a manual registry edit. The long-standing recipe is to open the plug-in's key
under `Software\McNeel\Rhinoceros\<version>.0\Plug-ins\{id}` and set `PlugIn\FileName` to the
`.rhp` you want loaded. That is also the shape Rhino writes itself: after loading a plug-in,
Rhino records the file it loaded back into that same value, which is the write-back ADR 0015
added the post-exit correction for. So the value is not a shape RWL would be inventing; it is
the one both Rhino and its users already write.

Two earlier findings could be mistaken for evidence against doing this, and neither applies.
ADR 0012 found that a hand-built complete registration is silently ignored: that was about
making Rhino load a plug-in ID it had never installed, by assembling a registration from
nothing. ADR 0014 deleted a redirect shape for the same reason. Here the registration already
exists, Rhino installed it, and only the file it names changes.

The honest limit is that RWL has not live-verified a Rhino start after such a switch. The
mechanism is the shape Rhino writes and the shape the manual recipe writes, but that is
inference from two observations, not a test of this code path.

## Decision

The standing registration is a named concept: the registration Rhino resolves for one
(Rhino version, plug-in ID) pair outside any launch, machine hive over current-user, in
either the installed shape (`PlugIn\FileName`) or the seed shape (root `FileName`). The
registered worktree is the one whose tree contains the file it names, by longest path match,
so a worktree nested under the primary checkout wins over the primary. Reading it is
read-only and therefore allowed in a launcher host; one reader serves the lease and every
surface, so they cannot disagree about what is registered.

Switching it is a registry mutation and therefore runs only in the launch executor, in a
process the interactive Windows shell started, under ADR 0015's rule. It is a new executor
mode rather than a new process kind, and it reuses the existing request shape, so an executor
from a release that predates it refuses the mode by name instead of misreading it.

The switch takes the same lock the lease takes, because one component owns everything
registered for a (Rhino version, plug-in ID) pair (ADR 0014). Under that lock it:

- refuses when a launch journal is pending, and does not restore it. A pending journal can
  belong to an executor still lingering behind a live Rhino, whose post-exit correction would
  put the old path back over this write. The refusal says to close that Rhino, or to launch
  again so RWL restores the journal, and then retry.
- decides its target once, the way Rhino resolves the ID: a machine registration wins,
  otherwise the current-user one, otherwise a new current-user install seed holding exactly
  `Name` and `FileName`. Nothing is displaced, so no load mode is invented (ADR 0014).
- writes in place, into whichever value the existing registration already names its file
  with. No key is deleted or recreated, the other hive is never touched, and no journal is
  written, because there is nothing to restore.
- refuses a machine registration this account cannot write, reusing the launch's conflict
  refusal: the registered path, the exact key, and both remedies. RWL never elevates.

Nothing depends on a write this process cannot prove is real. An independent registry probe
reads the written value back, and a reader that disagrees ends the operation as
`plugin_registration_not_visible` stating what it saw. The write is left in place there,
because it may well be real and removing it would be a second unproven write.

The change is published on the desktop as a per-row SET DEFAULT action on the selected,
launchable, not-yet-registered worktree, and on the CLI as `rwl registration set --path`. It
is not published on MCP: an agent has no reason to change what a person's ordinary Rhino
session loads.

## Consequences

- The desktop tells the truth about which build Rhino loads outside RWL, and offers to change
  it, so the manual registry edit is no longer the only way.
- Switching never builds. The artifact must exist, and a missing one fails with the direct
  launch's own diagnostic before an executor is started, so Rhino is never pointed at a file
  that is not there.
- The switch is durable by design, which is the opposite of every other registration RWL
  writes. It survives launches, restores, and the post-exit correction, because it is the
  pre-state those restore to. A user who forgets it will keep loading a worktree build from
  ordinary Rhino sessions; the DEFAULT chip is what makes that visible.
- Where the machine hive holds the registration, the switch needs write access to the machine
  `Plug-ins` key, the same grant ADR 0013 describes. Without it the operation refuses and
  names both remedies.
- A pending launch journal blocks the switch rather than being cleaned up by it. That is a
  refusal a user can hit legitimately, with a Rhino open, and its message has to be actionable
  rather than merely correct.
- The mechanism is not live-verified. It is checked at the registry level, by tests that
  assert the exact value written in each shape, and by an independent reader at run time.
  Confirming that Rhino then loads the switched build is a live test still to be run.
