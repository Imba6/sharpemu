# Stage 10B — BAD capture analysis: the blocker is an import-loop-guard false-positive on a preload `scePthreadYield` spin, NOT the module_start/equeue hypothesis

Status: **analysis + analyzer/parser improvement only, no production runtime change** (branch
`gpt-dlsym`, not pushed by this session). The Windows-native BAD capture
(`real-tests/Cocoon/PPSA08766-app0/run_stage10b_bad.log`, 392,723 lines, 18.4M imports) was
analyzed end to end. It **disproves the Stage-10B module_start/equeue hypothesis** and identifies a
different blocker.

## 1. Analyzer result

`scripts/analyze_stage10b_capture.py run_stage10b_bad.log` (extended this pass):

- **No stalled module_start** — every `module_start.begin` has a `module_start.complete`.
- Terminal outcome: `first_frame_presented=1`, `suspendPoint(gameplay proxy)=2` → **did NOT reach
  sustained gameplay** (good run `run_fix.log` = 33).
- `IMPORT-LOOP GUARD FIRED @L391226: import#18371840 nid=T72hz6ffq08 (scePthreadYield)
  ret=0x8006B8B5B` → forced guest unwind @L391496 → `Native backend FAILED: unknown backend error`
  @L392700 → `NOT_IMPLEMENTED / NativeBackendUnavailable`.

## 2–5. Path-B module_start completion / stalled module / causal equeue

| module | module_start.begin | module_start.complete | elapsed_ms |
|---|---|---|---|
| **PSNCore.prx** | seq=3 | ✅ L7197 | 1 |
| **PSNCommon.prx** | seq=5 | ✅ L7212 | 0 |
| **SaveData.prx** | seq=10 | ✅ L7523 | 0 |

Plus 6 more on-demand modules complete later (`BOB.prx`, `modnet.prx`, `weather.prx`, `wither.prx`,
`wobble.prx`, `K88.prx`). **Stalled module: NONE. Causal equeue: NONE.** The Stage-10 hypothesis
(Path-B `module_start` blocks on an equeue producer that never runs) is **disproven for this
capture**: all module inits complete in ≤1 ms.

## 6–10. There is no equeue producer-starvation in play

Because no `module_start` stalled, phases 6–10 (awaited event / expected producer / event produced /
reached queue) are **not applicable** — the module/equeue chain is not the blocker. Equeue traffic in
the run is normal: display (`filter=-13`) and graphics (`filter=-14`) events flow to `GfxFlipThread`
throughout; `equeue.wait-resume` shows waiters being woken.

## 11. pthread mutex ping-pong classification — SLOW PROGRESS, not a livelock

The `Loading.PreloadManager` ↔ `Job.Worker 7` `pthread_mutex_lock` (NID `9UK1vLZQft4`, 51,294 hits)
+ `sceKernelWaitEventFlag` (`JTvBflhYazQ`) churn is the Unity job-system + async asset-streaming
pipeline **making slow forward progress**, not a deadlock/livelock:

- Distinct assets load over time: `globalgamemanagers` → `sharedassets0..74` → `level0..87`
  (50+ distinct level files); `level72` still loading in the final window before the guard fired.
- Semaphore wake keys change (`004F`, `02B9`, `012A`, …) and a guest thread even **exits**
  (`Thread-…554E0` returned @L369780).
- `import#` climbs monotonically to 18.4M; event-flag broadcasts continue to L384544.

So the job/mutex activity is legitimate contention under emulation, not a stuck cycle. It is *slow*
(each mutex/sema/event-flag op is an HLE round-trip, amplified by the verbose per-op
`SHARPEMU_LOG_GUEST_THREADS` logging this capture enabled).

## 12. Exact first abnormal event

`L391226 [LOADER][ERROR] Import-loop guard fired at import#18371840: nid=T72hz6ffq08
ret=0x00000008006B8B5B`. The **Unity main thread** (`Thread-3`, `managed=2` — the thread that created
the VideoOut equeues `0x2/0x3/0x5`) was in a tight **`scePthreadYield` spin-wait** at
`ret=0x8006B8B5B` (inside eboot, base `0x800000000`). It is a normal "main thread waits for
background loading" spin. `scePthreadYield` (`T72hz6ffq08`) is **not** in the import-loop-guard
boundary list (`DirectExecutionBackend.Imports.cs:1903` — which resets on `sceKernelUsleep`,
`sceVideoOutWaitVblank`, `scePthreadCondWait/Timedwait`, `pthread_cond_wait/timedwait`), so its stable
signature accumulated. After **`DefaultImportLoopGuardSeconds = 5`** (`DirectExecutionBackend.cs:38`)
of continuous spin (`ShouldForceGuestExitOnImportLoop`, `Imports.cs:1852-1901`), the guard patched the
thread's return slot to a host-exit stub and force-unwound it (`Guest returned: 1202156176` @L391494;
`Detected repeating import loop and forced guest unwind to host` @L391496).

## 13. Actual source-level reason the native backend returned

Sub-agent source audit (confirmed against source):

- After the guard unwound the main thread, the session quiesced and
  `DirectExecutionBackend.ExecuteEntry` did not hit its success condition (`num6 == 0`,
  `DirectExecutionBackend.cs:6988`), so `TryExecute` returned `false`.
- `CpuDispatcher.DispatchEntryCore` (`CpuDispatcher.cs:321-343`) maps any `TryExecute==false` to the
  literal fallback string `"unknown backend error"` (when `LastError` is null/blank) →
  `ORBIS_GEN2_ERROR_NOT_IMPLEMENTED` + `CpuExitReason.NativeBackendUnavailable`.
- The `Summary:` counters `instr=0 imports=0 unique_nids=0` and `last_guest_rip=0x800000070` /
  `inst=push rbp` are **hardcoded placeholders** in `FailEarly` (`CpuDispatcher.cs:158-169`), not
  evidence that nothing ran.

## 14. Why LastError was null

The import-loop guard *does* set `LastError` ("Detected repeating import loop … forced guest exit",
`Imports.cs:1792`), but every secondary guest-execution context brackets the **shared** `LastError`
field with `save → null → restore` (`DirectExecutionBackend.cs:6025/6033/6112`, `4586-4618`,
`4824-4850`). A worker/continuation `finally` that restores a `previousLastError` captured while the
field was null **clobbers the guard's message back to null** before `Execute END` reads it. Net: the
real cause (guard-forced unwind) is lost and the dispatcher prints the generic fallback. **This is a
diagnostic-loss bug** — the tail *looks* like a mysterious backend failure but is a masked
import-loop-guard termination.

## 15. Is NOT_IMPLEMENTED the root cause? No — fallback only

`ORBIS_GEN2_ERROR_NOT_IMPLEMENTED` / `NativeBackendUnavailable` is a catch-all bucket for "native
backend returned no success result." It is **not** an unimplemented feature and **not** a crash (no
`AccessViolation`, no `__fastfail`, no `Exception`, no `MEMORY_FAULT` anywhere in the log). The true
cause is the guard firing (item 12).

## 16. GfxFlipThread equeue handle 0x5 — TEARDOWN only

The repeated `equeue.wait-timeout handle=0x5 timeout_usec=500000 thread='GfxFlipThread'` lines are
**all after `Execute END` (L392699)** — the render thread surviving into teardown, polling for a
vblank/flip that will not come because the backend has stopped. **Consequence, not cause.**

## 17. The 4 surviving workers — TEARDOWN consequence

`4 guest worker(s) … did not leave guest code during shutdown` (@L392721) is a **teardown
consequence**: the main thread was force-unwound while job/loading workers were still mid-flight, so
the runtime keeps the address space mapped to avoid faulting them. Not the cause.

## 18. Shader `f3dg2CSgRKY` relevance — ABSENT

`f3dg2CSgRKY` and `ERROR_MEMORY_FAULT` do **not appear** in this log. The earlier shader-memory-fault
sequence is from a different session and is **not part of this capture's timeline**. No GPU milestone
is warranted from this evidence.

## 19. First good-vs-bad divergence

Both run identical through module loading and first frame. The divergence is **after first frame, in
the preload/gameplay transition**: `run_fix.log` completes preload and reaches sustained gameplay
(`suspendPoint=33`, pipeline cache saved); the BAD run stays in preload (`suspendPoint=2`), the main
thread spin-yields, and at ~5 s the import-loop guard force-unwinds it. Note `run_fix.log` did **not**
run with `SHARPEMU_LOG_GUEST_THREADS=1`, so its preload was not slowed by per-op logging.

## 20. Revised root cause

The captured failure is an **import-loop-guard false-positive**: Cocoon's Unity main thread spin-waits
for asset preload via `scePthreadYield` (a non-boundary NID); preload progresses but slower than the
5 s guard window (heavily amplified by the `SHARPEMU_LOG_GUEST_THREADS=1` capture logging), so the
guard trips, force-unwinds the main thread, and the session collapses — then gets misreported as
"unknown backend error / NOT_IMPLEMENTED" (a fallback bucket, with the guard's real `LastError` lost to
a shared-field race). Two distinct defects:

1. **Guard scope**: `scePthreadYield` is not a loop-guard boundary, so a legitimate cooperative
   yield-spin-wait for background loading trips a 5 s hang watchdog even while other threads make
   progress.
2. **Diagnostic loss**: a guard-forced unwind surfaces as `LastError=null` → "unknown backend error",
   hiding the real cause.

Caveat (do not over-claim): the capture cannot prove preload would have *completed* — only that it was
still progressing when the guard killed it. The verbose diagnostic config likely contributed to
crossing the 5 s threshold.

## 21. Stage-10B hypothesis: DISPROVEN

Path-B `module_start` does not stall and no equeue producer-starvation occurs. The Stage-10 /
Stage-10B module-load + equeue hypothesis is **disproven for this capture**.

## 22. Next smallest milestone

**Re-capture to separate "guard false-positive" from "preload never completes", with the guard out of
the way and the per-op logging off:**

```
SHARPEMU_NATIVE_GUEST_V2=1 SHARPEMU_EXPERIMENT_0X1E_RENTED_WORKER=1 SHARPEMU_NATIVE_WORKER_MAX=128 \
SHARPEMU_DISABLE_IMPORT_LOOP_GUARD=1 SHARPEMU_LOG_LOADSTART=1   # NO SHARPEMU_LOG_GUEST_THREADS
```

- If Cocoon then completes preload and reaches sustained gameplay (`suspendPoint` climbs, draws
  advance) → the guard is the blocker (false-positive). Candidate fixes (next pass, one only, with a
  regression first): add `scePthreadYield` to `IsImportLoopGuardBoundary`, or make the guard
  progress-aware (do not fire while other guest threads' import signatures keep advancing). Also
  consider preserving the guard's `LastError` to the dispatcher (diagnostic fix).
- If it still never finishes preload with the guard disabled → the real blocker is preload
  throughput/correctness (investigate whether the rented-worker experiment slows the Unity job
  system vs the inline path that reached gameplay in `run_fix.log`).

Per the milestone: **no production runtime change this pass.**

## 23. Tests / analyzer changes

- `scripts/analyze_stage10b_capture.py`: added terminal-outcome parsing (import-loop guard firings
  with NID resolution, forced-unwind, backend-failure fallback, first-frame + `suspendPoint` gameplay
  proxy) and corrected the verdict so a run whose modules complete but is guard-killed is reported as
  DISPROVEN-hypothesis + FAIL-run, not "GOOD".
- `scripts/test_analyze_stage10b_capture.py`: 3 parser regressions pinned to the real
  `run_stage10b_bad.log` / `run_fix.log` shapes (guard-fire capture not called good; good run reaches
  gameplay; module-stall still detected). All pass.

## 24. Commits (branch `gpt-dlsym`, not pushed)

1. analyzer terminal-outcome extension + parser regression tests.
2. this analysis document.

Raw 340k-line log not committed.

## 25. git status

Branch `gpt-dlsym`. Modified: `scripts/analyze_stage10b_capture.py`. New:
`scripts/test_analyze_stage10b_capture.py`, this doc. The BAD log lives under
`real-tests/Cocoon/PPSA08766-app0/` (not committed).

## 26. NOTHING WAS PUSHED BY THIS SESSION.

## 27. Previous `origin/gpt-dlsym` advances were the user's manual checkpoint pushes.
