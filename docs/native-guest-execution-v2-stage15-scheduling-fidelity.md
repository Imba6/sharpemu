<!--
Copyright (C) 2026 SharpEmu Emulator Project
SPDX-License-Identifier: GPL-2.0-or-later
-->
# Native Guest Execution — Stage 15: scheduling-fidelity experiment (Cocoon rendezvous)

Branch `gpt-dlsym`. **Experimental routing only, behind an explicit env gate; no
default behavior change.** This host is Linux; the Windows Cocoon run matrix
(Phases 3–9) is prepared but NOT executed here.

## Goal

Test whether the Cocoon startup wedge (Stages 14a–14h: a cross-substrate
bounded-queue deadlock — producer `Loading.PreloadManager` on a native worker,
consumer = host-parked primary/main `Thread-3`) disappears or materially changes
under a **symmetric** guest-execution substrate.

## Phase 1 — experiment matrix

| Mode | Description | Env |
|---|---|---|
| **A** default | V2 off (inline primary + inline pthreads) | *(none)* |
| **B** current V2 | ordinary pthreads → native workers; primary = host-park | `SHARPEMU_NATIVE_GUEST_V2=1` |
| **C** symmetric native | as B, **plus** primary = dedicated persistent native worker (Stage-5C) | `SHARPEMU_NATIVE_GUEST_V2=1` `SHARPEMU_NATIVE_GUEST_V2_PRIMARY=1` |

Mode D (primary native + producer routing tweak) is deferred — no clean existing
mechanism routes `Loading.PreloadManager` specifically without a broader change,
which this milestone forbids. A/B/C are the core experiment.

## Phase 2 — gating (implemented, verified)

Mode C reuses the **proven Stage-5C** implementation
(`docs/native-guest-execution-v2-stage5c-dedicated-primary-worker.patch`),
reapplied onto current HEAD (it did not apply cleanly due to context drift; ported
by hand). It is gated by `SHARPEMU_NATIVE_GUEST_V2_PRIMARY=1` (requires
`SHARPEMU_NATIVE_GUEST_V2=1`):

- `NativeGuestV2PrimaryEnabled = NativeGuestV2Enabled && env=="1"`.
- When unset: `ExecuteEntryCooperativePrimary` is never taken, no thread has
  `IsCooperativePrimary`/`PinnedNativeExecutor`, and the guard-eligibility and
  `RunGuestEntryStub` changes reduce to the prior behavior. The `worker.Run` →
  `InvokeNativeGuestExecutor` extraction is a pure refactor.
- **Verified default-safe:** builds clean; 1132/1134 unit tests pass (the 2
  failures are the pre-existing environmental `KernelHeapCompatMemory`
  malloc-host-fallback tests, unrelated).

Reused Stage-5C? **Yes** — verbatim `PrimaryWorker.cs` plus the 4 supporting
edits, all dependency symbols verified present in HEAD.

## Phase 3 — Windows run matrix (to execute)

Per mode, ≥10 runs. Minimal diagnostics (sync snapshot as the oracle; add the
compact analyzers only as needed).

```powershell
# Mode A (default)
dotnet run --project .\src\SharpEmu.CLI -c Debug -- .\real-tests\Cocoon\PPSA08766-app0\eboot.bin *> run_s15_A_%i.log

# Mode B (current V2)
$env:SHARPEMU_NATIVE_GUEST_V2=1
$env:SHARPEMU_DIAG_SYNC_SNAPSHOT=1 ; $env:SHARPEMU_DIAG_STALL_SNAPSHOT_MS=3000
dotnet run --project .\src\SharpEmu.CLI -c Debug -- .\real-tests\Cocoon\PPSA08766-app0\eboot.bin *> run_s15_B_%i.log

# Mode C (symmetric native primary = Stage-5C)
$env:SHARPEMU_NATIVE_GUEST_V2=1
$env:SHARPEMU_NATIVE_GUEST_V2_PRIMARY=1
$env:SHARPEMU_DIAG_SYNC_SNAPSHOT=1 ; $env:SHARPEMU_DIAG_STALL_SNAPSHOT_MS=3000
dotnet run --project .\src\SharpEmu.CLI -c Debug -- .\real-tests\Cocoon\PPSA08766-app0\eboot.bin *> run_s15_C_%i.log
```
For Mode C confirm the log shows `V2 cooperative primary registered handle=…
(pinned native worker)` (proves the gate engaged). Record per run: first frame,
handshake begins?, #0x84/0x86 rounds, terminal cycle?, sustained gameplay, fps,
time-to-wedge, UCO, GC pathologies, AV/crash, worker peak/live, primary &
PreloadManager substrate.

## Phase 4 — pass/fail oracle

Authoritative = the sync snapshot via the offline tool:
```bash
python3 scripts/analyze_waitfor_graph.py run_s15_<mode>_<i>.log
```
- **FAIL:** cycle `['Thread-3','Loading.PreloadManager']` (bounded-queue wedge).
- **PASS:** no such cycle **and** guest progresses to sustained gameplay. A
  changed semaphore *handle* alone is NOT success.

Also useful: `scripts/analyze_run_report.py` (first-blocker + presents) and
`scripts/analyze_semaphore_timeline.py --handle 0x86`.

## Phase 5–7 — decision framework (to apply after runs)

- **B wedges, C progresses (≥10 runs, strong majority gameplay, no cycle):**
  cross-substrate scheduling is strongly causal. Do **not** promote C yet —
  first identify the smallest generic semantic that changed (ready-queue
  fairness, cooperative yield of the primary, nested-block behavior, host-park
  polling vs continuation, producer/consumer timing). Then Phase 8.
- **C also wedges similarly:** **STOP.** Cross-substrate hypothesis weakened;
  Unity guest logic remains the blocker (consistent with the Stage-14h finding).
- **Only random timing shifts success with no identifiable semantic:** **STOP.**

## Phase 8–9 — synthetic regression & production bar (gated on the above)

Only after a *generic* scheduling semantic is identified: build a title-agnostic
guest-thread regression (primary consumer + worker producer + capacity-1
rendezvous + repeated process/yield) demonstrating the liveness difference, then
the smallest generic fix. Production promotion requires the full bar (§Phase 9 of
the milestone) incl. cross-title validation — **not** merely "Cocoon starts."

## Status / what this session delivered

- Mode C (Stage-5C) reapplied, gated, compiled, and verified default-safe.
- Experiment matrix, oracle, and Windows commands prepared.
- The A/B/C runs, the B-vs-C comparison, and any fix are **pending Windows
  execution** (not runnable on this host).

## Next blocker

Execute the Phase-3 matrix on Windows and apply the Phase-5 decision. Until then,
no scheduling semantic is proven and **no production change is warranted**.

## Confidence ledger (Stage 15)

| Claim | Level |
|---|---|
| Mode C = faithful reapplication of proven Stage-5C, gated | **PROVEN** (compiles; deps verified) |
| Gate-off is behavior-preserving | **PROVEN** (1132/1134 tests; pure refactor otherwise) |
| Cross-substrate scheduling is causal | **UNKNOWN** — needs the B-vs-C run |
| A generic scheduling defect exists | **UNKNOWN** — needs runs then a synthetic regression |
