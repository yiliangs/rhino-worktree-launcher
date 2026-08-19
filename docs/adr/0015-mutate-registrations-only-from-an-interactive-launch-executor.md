# 0015: Mutate registrations only from an interactive launch executor

## Status

Accepted. Amends ADR 0012, ADR 0013, and ADR 0014 in one respect: the registration
mechanism they describe is unchanged, but no launcher host process may execute it. It
supersedes the Rhino launch broker introduced alongside ADR 0009's bootstrap, which is
deleted rather than kept beside the executor. ADR 0012 and ADR 0014 carry 2026-08-18
amendments scoping their live-test conclusions to hosts whose current-user registry writes
reach the registry, which is the condition this decision stops assuming.

## Context

A launch that never loaded anything was traced on 2026-08-18 to a property none of the
earlier decisions could have anticipated: the process performing the registry write was not
writing to the registry.

The MCP server, when spawned by its client, ran with its current-user registry writes
intercepted. The shipped `rwl-mcp.exe` created the install seed, read the key back, and saw
exactly what it had written. An external sampler polling every 100 ms never observed that
key exist, across several launches. The same release's `rwl-cli.exe`, started from an
ordinary shell, wrote a seed that was externally visible and loaded the worktree plug-in in
15 to 24 seconds. The machine hive passed through in both processes: HKLM reads and writes
were real. The process ran as the same user at high integrity, with no AppContainer, no UAC
virtualisation, and no restricted token, and the shipped binary decompiled identically to
its source. The interception is per-process and confined to the current-user hive.

Two consequences follow, and the second is the one that mattered. Every launch from that
host was unwinnable, because Rhino resolves the plug-in from a hive the seed never reached.
And every such launch was silent: the writing process could ask itself whether the seed was
there and would always be told yes. The only symptom was the ADR 0002 verification wait
running to its timeout, which reports that Rhino never mapped the artifact, not why.

One part of the system was already outside the interception. ADR 0009's bootstrap starts
Rhino through a shortcut handed to `explorer.exe`, so the Rhino process is resolved by the
interactive shell rather than inherited from the launcher's process chain. That mechanism
existed to give Rhino a clean environment; it turns out to be the boundary that matters.

Two further defects were observed in the same session. Rhino runs elevated on this machine
and, after loading a plug-in, writes the artifact's path back into the machine registration.
ADR 0014's restore runs at verification, while that Rhino is still running, so the machine
registration could end a session naming a worktree artifact and the user's next ordinary
Rhino start would load a stale worktree build. Separately, a launch that queued behind
another session's lock blocked with no diagnostics and expired as a generic timeout.

## Decision

No registry mutation runs inside a process that can be sandboxed. All of it moves into one
launch executor process that the interactive Windows shell starts.

The launcher host keeps what it can prove: resolving the worktree, building the canonical
solution, and naming the artifact. Everything after that, pending-journal recovery, the
ADR 0014 lease across both hives, the Rhino start, ADR 0002 verification, and the restore,
belongs to the executor. MCP, CLI, and desktop become thin clients of it, and they differ
only in the host kind they record.

The two halves speak a versioned newline-delimited JSON protocol over a private pipe: one
request, a stream of progress events, exactly one terminal result. A version mismatch is a
named error rather than a misread field, because both halves ship in one release and a
mismatch means the installation is half-updated. The executor writes its own JSONL log per
launch, which the launch log names, so a failure inside that process is readable from
outside it.

Rhino is started by the executor directly. The executor is already in interactive context,
so the second hop through the shell is unnecessary, and the broker that performed it is
deleted; the reusable half, spawning through an explorer shortcut, becomes the executor's
own spawn mechanism.

**Nothing depends on a registration the launch cannot prove is real.** The executor writes
the seed, then asks a separate process what it can see, and only then starts Rhino. The
lease writes a per-launch nonce beside the seed and removes it once the reader has confirmed
both the nonce and the file name, so Rhino still reads exactly ADR 0012's documented install
seed, and an identical seed left in the real hive by an earlier launch cannot be mistaken
for this one. Where ADR 0014's 2026-08-18 amendment writes no seed at all, the same check
runs against an RWL-owned key, because the clearing of the current-user key has to be real
too. A seed no independent reader can see ends the launch before Rhino starts, with
`registry_seed_not_visible` and the remediation, and restores.

The reader is a bootstrap mode, `rwl.exe registry-probe`, answering one registry read over a
pipe. It is answered by the bootstrap rather than the current release, so it shares no code
with the writer and still answers on a half-updated installation. The executor spawns it
directly; `rwl doctor` spawns it through the interactive shell, because the condition doctor
checks for is the host's own writes being intercepted.

A host proves the whole chain once at startup rather than letting each launch discover it.
The MCP server pings an executor through the shell, in the background, in a mode that
touches no registration. A host that cannot complete that fails every launch immediately
with `interactive_spawn_unavailable`.

The launch's restore no longer ends the launch's claim on the namespace. The journal
survives it, and the executor lingers detached until the Rhino it started exits, then
compares both hives against the journaled pre-state and restores again if either drifted.
The journal is deleted only after that. Making that comparison well defined required one
change to ADR 0014's journal: the machine hive's pre-state is captured whether or not the
launch displaces it, because a null entry previously meant "not touched" and could not be
told from "did not exist". A separate flag decides whether the launch's own restore writes
that hive, so a launch that never needed write access there still does not.

Every failure is loud. Each terminal state has a diagnostic code naming the step that
failed, and a launch queued behind another reports `lease_wait` naming the holder recorded
beside the lock file, then ends as `lease_wait_timeout` rather than as a generic timeout.

## Consequences

- A launch from a sandboxed host now fails in seconds with the cause named, instead of
  running to the verification timeout with nothing to read. It still fails: this decision
  makes the condition visible and does not defeat the interception.
- One more process participates in every launch, and the launch's answer now depends on the
  interactive shell being reachable. `rwl doctor` and the host startup probe both check that
  directly, and both name it.
- Rhino no longer inherits the launcher host's environment. It inherits the shell's, which
  is the environment an ordinary Rhino start already gets, and the broker's
  environment-difference forwarding is gone with it.
- The executor outlives the launch it answered, until Rhino exits. It holds no lock during
  that time: it restores and releases at verification, and re-acquires briefly for the final
  correction.
- A pending journal is now the normal state of a live session rather than evidence of a
  crash. The next launch of the same plug-in restores it first, as before, which is also
  what corrects the machine registration if the lingering executor dies.
- The post-exit correction compares the registered path, not every value. A write-back that
  changes something else about the registration is not detected.
- The check proves that a write is visible to another process. It cannot prove Rhino will
  read that hive, and a future Rhino resolving registrations differently would need its own
  evidence.
- The interception itself is unexplained. It was established by observation on one machine
  on 2026-08-18, and the mechanism enforcing it was not identified.

## What this does not change

ADR 0012's install seed, ADR 0013's granted-access suspension of a competing machine
registration, ADR 0014's single journaled lease across both hives and its two amendments,
and ADR 0002's external file-use verification are all unchanged as mechanisms. This decision
changes only which process executes them, what must be proven before Rhino starts, and how
long the journal survives.
