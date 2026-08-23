<!--
Copyright (C) 2026 SharpEmu Emulator Project
SPDX-License-Identifier: GPL-2.0-or-later
-->
# Native Guest Execution — Stage 14b: live sync snapshot confirms the 0x86/0x84 cycle

Branch `gpt-dlsym`. Evidence: `run_stage14_snapshot.log` (fresh Windows V2-ON
capture, `SHARPEMU_DIAG_SYNC_SNAPSHOT=1 SHARPEMU_DIAG_STALL_SNAPSHOT_MS=3000`),
analysed with `scripts/analyze_waitfor_graph.py` (authoritative snapshot mode).
No runtime semantics were changed this stage.

## Baseline update (Windows, current HEAD)

- **V2-OFF default = 5/5 stable "0x86 never handshakes"** (loading stalls,
  `sceKernelWaitSema(0x86)` TIMED_OUT flood at RIP `0x800D18129`, producer never
  signals 0x86). Supersedes the historical 4/5 classification.
- **V2-ON = begins the handshake, then wedges.** The two are **distinct stable
  shapes** with no proven shared root cause.

## The live snapshot (authoritative)

The auto-trigger fired 4× (`reason=stall-3000ms`); the terminal block (seq 4)
holds 66 threads, 61 Blocked. Sema tracing was OFF, so the snapshot — not the
log — is the sole authoritative sync state.

**Deadlock pair (cycle CONFIRMED):**

| Role | Thread | Substrate | Waits on | Unblocked by |
|---|---|---|---|---|
| Producer | `Loading.PreloadManager` (`0x2bd49abc9e0`) | **native worker** | `sema 0x84` (infinite) | the 0x86 consumer (signals 0x84 @ `0x800D1379F`) |
| Consumer | `Thread-3` (`0x2bd49ab89a0`) | **host-park (primary/external executor — the Unity main thread)** | `sema 0x86` (1000 µs, 8367 timeouts) | `Loading.PreloadManager` (signals 0x86 @ `0x800D17D53`) |

- `sema 0x86`: count 0, waiters 1, **last-signaler = `Loading.PreloadManager`** (recovered).
- `sema 0x84`: count 0, waiters 1, **last-signaler = UNKNOWN** — because the 0x84
  signaler (the consumer) runs on the external executor and signals with guest
  handle 0. The offline tool therefore could not *auto-close* the cycle, but the
  identities + protocol structure confirm it. A diagnostic fix now names such
  external signalers on the next capture (see below).

**This resolves the Stage-13/14 open question:** the historically anonymous
`guest=0` 0x86 waiter is the **primary/external executor (the Unity main
thread)**, host-parked — recoverable only as the synthetic handle `Thread-3`
(its Unity-level name is genuinely not in emulator state; never fabricated).

## Routing / substrate

- **65 of 66 threads run on native workers**; **exactly one is host-parked** —
  the primary executor (`Thread-3`). V2-ON routes every ordinary guest pthread
  (including `Loading.PreloadManager`) onto native workers, leaving only the main
  entry thread on the host-park path.
- The deadlocked pair therefore **straddles two execution substrates**:
  native-worker producer ⇄ host-parked main-thread consumer.

## First permanently non-progressing context

**`Loading.PreloadManager`, blocked on `sema 0x84` (infinite).** It is stuck on
0x84 in *every* non-empty snapshot (seq 2/3/4). The consumer side is *not* a hard
deadlock: `Thread-3` polls 0x86 with a 1000 µs timeout and continues other
main-loop work between timeouts (it appears *Running* in seq 2/3; `GfxFlipThread`
keeps re-presenting, which is why the flip counter still ticked and fired 4
separate stall snapshots). So the terminal state is: **the producer is hard-stuck
awaiting an acknowledge that the still-alive-but-not-reciprocating consumer never
sends.**

## Does V2 routing contribute?

- **Yes, it creates the configuration**: routing `PreloadManager` to a native
  worker is what lets the handshake *begin* at all (V2-OFF never signals 0x86).
  It also makes the pair cross-substrate (worker producer, host-parked consumer).
- **But the primitive is not at fault** (consistent with Stage-13): the 0x86
  token persists and is consumed exactly once (count 0 with the consumer
  waiting ⇒ it consumed the last handoff), and there is no lost wake. The wedge
  is a **higher-level reciprocation failure**: the consumer consumed
  `PreloadManager`'s last 0x86 handoff but did **not** emit the reciprocal 0x84
  ack before re-waiting on 0x86, while `PreloadManager` had already advanced to
  its blocking `wait 0x84`. Whether V2's interleaving is the *root trigger* of
  that missed reciprocation, versus a protocol mismatch that would also occur
  inline, cannot be settled from this capture — it needs the per-signal timeline
  (see trace-back capture).

## Verdict

The expected **0x86 ⇄ 0x84 cycle is PRESENT** (not disproven). The earliest
*causal* divergence — the exact round where the reciprocal 0x84 ack is skipped —
is **not** reconstructable from this log because it carries no `sema.*` trace
(only the terminal snapshot + timeout flood). One more capture pins it.

## Trace-back capture (Windows) — pins the earliest divergence

```powershell
$env:SHARPEMU_NATIVE_GUEST_V2=1
$env:SHARPEMU_DIAG_SYNC_SNAPSHOT=1
$env:SHARPEMU_DIAG_STALL_SNAPSHOT_MS=3000
$env:SHARPEMU_LOG_SEMA=1                 # adds the per-signal 0x86/0x84 timeline
dotnet run --project .\src\SharpEmu.CLI -c Debug -- `
  .\real-tests\Cocoon\PPSA08766-app0\eboot.bin *> run_stage14b_trace.log
```
Then:
```bash
python3 scripts/analyze_semaphore_timeline.py run_stage14b_trace.log --handle 0x86
python3 scripts/analyze_semaphore_timeline.py run_stage14b_trace.log --handle 0x84
python3 scripts/analyze_waitfor_graph.py run_stage14b_trace.log     # snapshot: 0x84 signaler now NAMED
```
Expected with this build: the snapshot's `sema 0x84 last_signaler` is now the
consumer's synthetic handle (no longer UNKNOWN), so `analyze_waitfor_graph.py`
prints `*** DEADLOCK CYCLES DETECTED *** Loading.PreloadManager -> Thread-N`.
The `--handle 0x86`/`0x84` timelines give the last successful round and the exact
first skipped 0x84 ack — the earliest divergence.

## Confidence ledger (Stage 14b)

| Claim | Level |
|---|---|
| V2-OFF default = 5/5 "0x86 never handshakes" | **PROVEN** (Windows, current HEAD) |
| V2-OFF and V2-ON are distinct shapes | **PROVEN distinct** |
| 0x86 consumer = primary/external executor (main thread), host-parked | **PROVEN** (snapshot) |
| 0x86 producer = Loading.PreloadManager (native worker) | **PROVEN** (snapshot) |
| 0x86/0x84 mutual-wait cycle present | **PROVEN** (identities + structure) |
| First permanently-stuck context = PreloadManager on 0x84 | **PROVEN** (all snapshots) |
| Consumer is a *hard* deadlock | **DISPROVEN** (it polls + does other work) |
| Primitive lost a wake / dropped a token | **DISPROVEN** (token persists, consumed once) |
| V2 routing creates the cross-substrate config that enables the wedge | **PROVEN** |
| V2 interleaving is the *root trigger* of the skipped 0x84 ack | **POSSIBLE** — needs the trace-back timeline |
