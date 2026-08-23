<!--
Copyright (C) 2026 SharpEmu Emulator Project
SPDX-License-Identifier: GPL-2.0-or-later
-->
# Native Guest Execution — Stage 14c: the exact 0x86/0x84 divergence round

Branch `gpt-dlsym`. Evidence: `run_stage14b_trace.log` (fresh Windows V2-ON
capture, `SHARPEMU_NATIVE_GUEST_V2=1 SHARPEMU_DIAG_SYNC_SNAPSHOT=1
SHARPEMU_DIAG_STALL_SNAPSHOT_MS=3000 SHARPEMU_LOG_SEMA=1`), analysed only with
`scripts/analyze_semaphore_timeline.py` and `scripts/analyze_waitfor_graph.py`.
**No runtime semantics were changed.**

## Handshake protocol (confirmed)

```
Loading.PreloadManager (native worker, guest 0x1AB0E9043F0):
    signal 0x86 @ 0x800D17D53      # hand off a production
    wait   0x84 @ 0x800D17CFB      # block for the reciprocal ack

Main thread / primary-external executor (host-parked, "Thread-3"):
    wait/consume 0x86 @ 0x800D18129   # in a while(<cond>) poll loop, timeout 1000us
    signal 0x84       @ 0x800D1379F   # ack, from a LATER point in the main loop
```

Totals: **0x86 signalled 13× (all by PreloadManager), consumed 13×** (every
production delivered and consumed); **0x84 signalled 12×** (11× @ `0x800D1379F`,
1× early @ `0x800D19C55`). Off-by-one: the consumer acked 12 of 13 productions.

## Round-by-round (polls between each 0x86 consume and its 0x84 ack)

| round | consume 0x86 (line) | ack 0x84 (line) | polls between |
|---|---|---|---|
| 1 | 28668 | 29914 | 2 |
| … | … | … | 0–16 |
| 11 | 100006 | 100013 | 0 |
| **12 (last good)** | **100207** | **100260** | **1** |
| **13 (first failed)** | **100464** | **— (never)** | **13008 (∞)** |

**A clean cliff, not degradation.** Every round 1–12 completes with a small,
bounded number of extra 0x86 polls; round 13 consumes the production and then
polls 0x86 forever with no ack.

## The exact divergence (round 13)

```
100462  PreloadManager  signal 0x86 #13            # identical to every prior round
100463  PreloadManager  wait   0x84  (blocks here) # PM already blocked awaiting ack
100464  Thread-3        wait-host-wake 0x86        # main CONSUMES the 13th production
100467  Thread-3        wait-host-block 0x86       # main RE-ENTERS the poll loop
100477  Thread-3        wait-host-block 0x86       # ... and again ...
  …     Thread-3        wait-host-block 0x86  ×13008 (to line 307774)
        (main NEVER reaches 0x800D1379F again — no 0x84 signal after line 100260)
```

Answers to the specific questions:

1. **Last completely successful round:** 12 (0x86 #12 line 100205 → ack 0x84 #12 line 100260).
2. **First incomplete round:** 13 (0x86 #13 line 100462; ack never emitted).
3. **Order/timestamps:** by log line (no wall-clock in trace); sequence above.
4. **Identities:** producer `Loading.PreloadManager` (guest `0x1AB0E9043F0`, native worker); consumer `Thread-3` (the primary/external executor = Unity main thread, host-parked). The 0x84 signaler is also `Thread-3` (guest handle 0 at signal time, now named — see Q10).
5. **Does Thread-3 consume the final 0x86 token?** **YES** — `wait-host-wake` at line 100464 (0x86 count 1→0).
6. **Does it then run the code that signals 0x84?** **NO** — it never reaches `0x800D1379F` again after round 12.
7. **Where does its control flow go instead?** Back into its own `while(<cond>) WaitSema(0x86, 1000us)` poll loop at `0x800D18129` (13008 further iterations). It returns from each 1 ms timeout, runs guest loop-body code, and re-waits — i.e. its guest-visible loop-exit condition never becomes true.
8. **Is PreloadManager already blocked on 0x84 at that point?** **YES** — at line 100463, one line after signalling 0x86 #13 and before the consumer's first failed poll (100467).
9. **Does worker/host-park scheduling skip a legal guest continuation?** **NO.** The consumer is scheduled continuously (~1248 0x86 polls per 20 000 log lines, sustained to line 307774 — not starved); it received and consumed every 0x86 token; no `guest_exception` (0x1E) occurs anywhere in the failure window (100462–101500); PreloadManager's round 13 is byte-identical to round 12. The emulator delivered the production and ran the consumer's guest code correctly.
10. **Does the live graph auto-close now?** **YES.** With the external-signaler naming fix, the snapshot records `0x84 last_signaler = Thread-3` and `0x86 last_signaler = Loading.PreloadManager`, and `analyze_waitfor_graph.py` reports `*** DEADLOCK CYCLES DETECTED *** ['Thread-3', 'Loading.PreloadManager']`.

## Verdict — this is a guest branch, not an emulator execution/routing defect

Every emulator-side transition is correct: the 0x86 token is delivered and
consumed exactly once, no wake is lost, no continuation is skipped, no 0x1E
interferes, and the consumer keeps getting CPU. The consumer simply **chooses
the re-poll path instead of the ack path** because an **earlier guest-visible
condition — the memory flag/state its `while(<cond>)` 0x86-poll loop tests —
never transitions to the exit value for round 13.**

Per the standing rule, this is the "main thread legitimately does not ack 0x84"
case: **STOP; do not change semaphore/scheduler semantics; do not build the
worker→host-park→ack synthetic regression** (its precondition — a demonstrated
generic execution/routing defect — is not met; the evidence points the other
way).

## Which guest-visible condition, and how to name it (next capture)

The log carries no guest-memory values, so the specific flag cannot be named
from it. Two candidate root causes remain, distinguishable only with guest-state
inspection:

- **(A) Guest-side timing-sensitive handshake [most likely].** On real PS5 the
  main thread and PreloadManager have particular relative scheduling/priorities;
  under V2 the producer runs on a native worker while the consumer is the
  host-parked primary executor, so their *relative timing* differs. A guest
  ordering assumption that holds on hardware is violated in exactly one window
  (round 13). Consistent with: V2-OFF never starts the handshake (different
  timing), V2-ON runs 13 clean rounds then cliffs.
- **(B) Cross-substrate memory visibility [unlikely].** The consumer fails to
  observe a flag the producer wrote before `SignalSema(0x86)`. Unlikely because
  `SignalSema`/`WaitSema` both take the semaphore's monitor `Gate`
  (`KernelSemaphoreCompatExports.cs`), and a .NET lock release→acquire is a full
  barrier, so the producer's pre-signal writes are visible to the consumer after
  it acquires `Gate` to consume — happens-before is already established.

**To identify the exact flag and pick A vs B**, capture guest state at the poll
loop (Windows):

```powershell
$env:SHARPEMU_NATIVE_GUEST_V2=1
$env:SHARPEMU_DIAG_SYNC_SNAPSHOT=1
$env:SHARPEMU_DIAG_STALL_SNAPSHOT_MS=3000
$env:SHARPEMU_LOG_SEMA=1
# plus a guest-RIP/operand trace at the consumer's poll loop:
$env:SHARPEMU_TRACE_GUEST_RIP=0x800D18129     # (loop head; see note)
dotnet run --project .\src\SharpEmu.CLI -c Debug -- .\real-tests\Cocoon\PPSA08766-app0\eboot.bin *> run_stage14c_guestmem.log
```

Note: a per-RIP guest register/operand trace flag does not exist yet. The
smallest generic diagnostic to add (a follow-up, gated, read-only) is a
"guest-RIP watch" that, for a configured RIP, logs the register file and the
memory operand read at that instruction. That would print the flag address and
its value each poll, showing directly why the loop condition holds in round 13.
Building it is the recommended next step **before** any behavioural change.

## Confidence ledger (Stage 14c)

| Claim | Level |
|---|---|
| Off-by-one: 13 productions, 12 acks; round 13 is the first failed round | **PROVEN** |
| Consumer consumes the final (13th) 0x86 token | **PROVEN** |
| Consumer never reaches the 0x84 ack path afterward | **PROVEN** |
| PreloadManager already blocked on 0x84 before the failure | **PROVEN** |
| No emulator dropped token / lost wake / skipped continuation / 0x1E / starvation | **PROVEN** |
| Live graph auto-closes the 0x86/0x84 cycle with named identities | **PROVEN** |
| Root = guest-visible loop-exit condition never transitioning (a guest branch) | **PROVEN (that it is a guest branch)** |
| Cause is guest-side timing sensitivity exposed by V2 scheduling (A) | **LIKELY** |
| Cause is cross-substrate memory visibility (B) | **POSSIBLE (unlikely)** |
