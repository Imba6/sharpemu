# Stage 11B — guard-disabled Cocoon stall is `Baselib_SystemSemaphore` (0x86) producer starvation under the rented-worker experiment, NOT a semaphore defect

Status: **analysis + analyzer/parser improvement only, no production runtime change** (branch
`gpt-dlsym`, not pushed by this session). The guard-disabled Windows A/B run
(`real-tests/Cocoon/PPSA08766-app0/run_stage11_guardoff.log`, 12,721 lines) reaches the render loop
but never sustains gameplay. Root of the stall: a Unity thread waits forever on a semaphore that a
producer thread never posts. The semaphore layer is proven sound; the producer starvation is induced
by the `SHARPEMU_EXPERIMENT_0X1E_RENTED_WORKER` routing.

## 1–3. WaitSema quantification (Phase 1)

`sceKernelWaitSema` (NID `Zxa0VhQVTsk`) result lines in the bad log:

- **5,816 calls, 100% `ORBIS_GEN2_ERROR_TIMED_OUT`, 100% on handle `0x86`, 100% from caller RIP
  `0x800D18129`**, `needCount=1` (rsi=1), finite timeout (rdx = stack pointer to a µs value).
- First at **L2707 / import#2,742,828**; last at **L12720 / import#34,005,513** (end of log). The wait
  dominates the entire post-first-frame period and **never once succeeds**.
- No other handle and no other caller appears (the WARN only logs timeouts; successful/other waits
  are not in this log because `SHARPEMU_LOG_SEMA` was off).

## 4–5. Waiter thread, semaphore identity (Phase 2)

From the **good** run (`run_fix.log`, captured with `SHARPEMU_LOG_SEMA=1`):

```
sema.create handle=0x00000086 name='Baselib_SystemSemaphore' attr=0x1 init=0 max=2147483647
sema.signal handle=0x00000086 name='Baselib_SystemSemaphore' signal=1 count=1 waiters=1 guest=0x2C78913D0D0 ret=0x800D17D53
```

- **Semaphore 0x86 = Unity `Baselib_SystemSemaphore`** (`init=0`, `max=INT_MAX`, wake key
  `sceKernelWaitSema:00000086`).
- Caller `0x800D18129` (waiter) and `0x800D17D53` (signaler) are adjacent Baselib
  acquire/release call sites in eboot (base `0x800000000`) — a classic **producer/consumer handshake**
  (one Unity thread `Baselib_SystemSemaphore_Acquire`, another `_Release`).

## 6. Timeout semantics (Phase 4) — correct

Semaphore agent audit (confirmed from source, `KernelSemaphoreCompatExports.cs`): ABI `rdi=handle,
rsi=needCount, rdx=timeout*`; relative µs timeout; guest waiters take a **cooperative continuation
block** (`RequestCurrentThreadBlock` with a `WakePredicate` that decrements `Count`, and a `ResumeWait`
that returns TIMED_OUT on deadline). Fast path acquires immediately if `Count >= needCount`. This is
correct finite-wait behaviour; the repeated timeout-then-retry is a legitimate guest poll loop.

## 7–12. Signal side — the semaphore layer loses nothing; the producer never posts (Phases 3, 5)

**Proven by construction (agent audit):** `Count` lives in a process-wide dictionary independent of
which host/worker runs the producer. A `SignalSema` increment persists in `Count`; a **timeout does
not consume `Count`**; a posted token is consumed by the next wait's fast-path check. Wakes are keyed
by handle (`sceKernelWaitSema:00000086`) and matched exactly. Therefore a signal **cannot be lost at a
timeout boundary** — at most it costs one extra iteration.

⟹ **5,816 consecutive timeouts ⟹ `SignalSema(0x86)` is essentially never executed in the bad run.**
This is **CASE B — the producer never runs/posts**, not a semaphore-primitive defect (not a lost
signal, not a count race, not a wrong-waiter wake).

Corroboration from the **good** run: `Baselib_SystemSemaphore 0x86` is signaled **exactly once**
(`waiters=1`), the polling waiter acquires it on the fast path, and Cocoon proceeds to gameplay. Only
**one** post is needed to unblock the game.

## 13–16. Producer starvation is induced by the rented-worker experiment (Phase 6)

Clean good-vs-bad divergence:

| | GOOD (`run_fix.log`) | BAD (`run_stage11_guardoff.log`) |
|---|---|---|
| routing | **inline** (`native_run_enter`=0, no experiment markers) | `SHARPEMU_EXPERIMENT_0X1E_RENTED_WORKER=1` (rented workers) |
| sema 0x86 signals | **1** (producer runs) | **0** (producer never posts) |
| 0x86 waits | ~15 timeouts then acquire | 5,816 timeouts forever |
| outcome | sustained gameplay (33 suspendPoints, pipeline cache saved) | render loop only, no gameplay |

Both runs reach the **same** fence-wait at import#~2.74M; GOOD gets one post and proceeds, BAD never
does. **The rented-worker experiment is the differentiator** — the default inline path (which is what
`run_fix.log` uses) posts the fence and reaches gameplay.

**The agent's pool-exhaustion mechanism is DISPROVEN by the log:** rented-worker occupancy peaks at
`peak_concurrent=26` of `pool_max=128` with **zero rent timeouts**. There are 100+ free worker slots,
so the producer is **not** blocked on `NativeWorkerPool.Rent`. The producer thread is starved for a
different reason (never scheduled, blocked on a dependency, or takes a different path under
rented-worker routing) — **not yet identified**, because the guard-disabled capture had
`SHARPEMU_LOG_SEMA` and `SHARPEMU_LOG_GUEST_THREADS` off, so the signal side and the producer thread's
state are invisible in it.

## 17. Root cause (proven vs open)

- **Proven:** the Stage-11B stall is `Baselib_SystemSemaphore` (0x86) **producer starvation** — the
  Unity consumer thread polls the fence forever because the producer never `SignalSema`s it; the
  semaphore primitive itself is sound; the rented-worker experiment is the causal config; the default
  inline path posts the fence and reaches gameplay.
- **Open (insufficient evidence):** the exact reason the producer thread does not run/post under the
  experiment (it is **not** pool exhaustion). This needs the signal-side + producer-thread-state trace
  from the bad config.

Per the milestone, **no production fix** is made until that exact dependency is proven.

## 18. Higher-level progress before the stall (Phase 7)

Asset preload **completes** (last asset probe L6694: `common_assets_all.bundle`, `unity default
resources`; 87 distinct levels loaded). The render loop runs (31 `suspendPoint`,
`vk.ordered_action_fence_wait`). The last meaningful higher-level progress is the completion of
Addressables/StreamingAssets preload; immediately the game blocks on `Baselib_SystemSemaphore 0x86`
(scene activation / job-completion handshake) and never advances.

## 19. Analyzer / parser changes (this pass)

- `scripts/analyze_stage10b_capture.py`: added a **stuck-fence detector** — a run-spanning
  repeated-`TIMED_OUT` flood on one `(nid, handle, caller)` (default ≥200) is reported as a STUCK
  FENCE POLL and **overrides the weak `suspendPoint` gameplay proxy** (this capture emits 31
  suspendPoints — as many as the good run's 33 — yet stalled, so suspendPoint alone is not a gameplay
  proof). It now prints `nid=Zxa0VhQVTsk (sceKernelWaitSema) handle=0x86 caller=0x800D18129 timed out
  5816x` and classifies the run as NOT reaching gameplay.
- `scripts/test_analyze_stage10b_capture.py`: +2 regressions (stuck fence overrides suspendPoint; a
  few timeouts are not a stuck fence). 7 tests total, all pass.

## 20. Production fix

**None** (unproven exact dependency; the semaphore primitive is correct — a fix there would be forcing
success). No guard change either (kept separate per the milestone; the import-loop-guard false-positive
is a distinct, still-valid robustness issue documented in stage-10b).

## 21–23. Next smallest milestone (two narrow Windows captures; no verbose guest-thread flood)

1. **Producer-fate capture (primary):** BAD config **+ `SHARPEMU_LOG_SEMA=1`** (do NOT enable
   `SHARPEMU_LOG_GUEST_THREADS`). Log to `run_stage11b_sema.log`. This shows whether
   `sema.signal handle=0x00000086` ever fires, and — via the `guest=`/`ret=` on every wait/signal —
   what the producer-side thread (`ret=0x800D17D53` region) is doing instead of posting (e.g. blocked
   on another semaphore). If the producer is blocked on a mutex/equeue rather than a sema, follow up
   with the matching narrow trace.
2. **Experiment-causality A/B (decisive, cheap):** current HEAD, BAD config **but with
   `SHARPEMU_EXPERIMENT_0X1E_RENTED_WORKER` removed**, everything else identical, no verbose flags.
   Log to `run_stage11b_noexp.log`. If Cocoon reaches sustained gameplay, the experiment is causally
   proven as the blocker (matching `run_fix.log`), and the decision becomes whether the Stage-7
   rented-worker experiment is still needed at all now that the 0x1E/GC-suspend issues are fixed.

The analyzer already classifies both outcomes (STUCK FENCE / reached-gameplay).

Only after (1)/(2) prove the exact producer dependency should a fix be considered, and only if it is a
unique generic emulator defect (e.g. a scheduler/routing path under the experiment that fails to run a
runnable producer). Not allowed: forcing sema success, waking all, ignoring the timeout, or a
0x86-specific hack.

## 24. git status

Branch `gpt-dlsym`. Modified: `scripts/analyze_stage10b_capture.py`. New:
`scripts/test_analyze_stage10b_capture.py` (updated), this doc. The bad log lives under
`real-tests/Cocoon/PPSA08766-app0/` (git-ignored, not committed).

## NOTHING WAS PUSHED BY THIS SESSION.

## Previous `origin/gpt-dlsym` advances were the user's manual checkpoint pushes.
