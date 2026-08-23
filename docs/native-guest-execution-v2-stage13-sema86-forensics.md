<!--
Copyright (C) 2026 SharpEmu Emulator Project
SPDX-License-Identifier: GPL-2.0-or-later
-->
# Native Guest Execution — Stage 13: Baselib semaphore 0x86 forensics

Autonomous session, branch `gpt-dlsym`, baseline commit `17687b7`.

This stage attacks the **Baselib `Baselib_SystemSemaphore` 0x86 stall** with reusable
tooling instead of hand-grepping, cross-checks the log evidence against fresh
read-only audits of the semaphore primitive, the GC-suspend / host-park
machinery, and the import-loop guard, and lands several small, tested, generic
fixes plus new diagnostics.

**Nothing in this stage is title-specific.** No semaphore handle, NID, or thread
name is special-cased in any shipped code path. The Cocoon logs are only used as
the corpus that the generic tools consume.

---

## 1. Executive summary

- The 0x86 stall is **not** a broken semaphore primitive and **not** a lost
  wakeup. In every V2-on capture the semaphore delivered *every* signal to its
  waiter (`signal == wake`, exactly). **PROVEN.**
- The producer of 0x86 is the **`Loading.PreloadManager`** guest thread, signal
  site `ret=0x800D17D53`; the waiter is host-blocked at `ret=0x800D18129` with a
  finite 1000 µs timeout (so it *cannot* lose a wake permanently — it re-checks
  every ~1 ms). **PROVEN.**
- The terminal state is a **higher-level producer/consumer wedge** in Unity's
  asset-loading handshake (`0x86` forward + `0x84` acknowledge), which after
  ~6–8 rounds stops making mutual progress. It is accompanied by a broad
  multi-thread stall (render thread `UnityGfxDeviceWorker` blocks on `0x56`) and
  by **hundreds of multi-second IL2CPP stop-the-world GC pauses** (526/532/600
  parked GC-suspend deliveries > 1 s per run, one measured at 6.285 s). The GC
  pauses are a **symptom** of threads unable to reach safepoints once wedged, not
  an independent primitive bug. **PROVEN (structure); LIKELY (causal ordering).**
- `sce::Agc::suspendPoint` is a **no-op export**; the `"Forcing call to
  sce::Agc::suspendPoint / TRC R4089"` log line is emitted by the guest's own
  libSceAgc and has **zero** connection to CPU-thread suspension. It was a red
  herring. **PROVEN.**
- There are **two distinct failure shapes** of the 0x86 stall depending on
  configuration (see §4): V2-on signals 6–8× then wedges; V2-off (default) never
  signals 0x86 at all and floods timeouts from the first handshake with **zero**
  GC pauses. These are different failures and must not be conflated.
- The import-loop guard has a genuine, generic defect (progress-blind + reason
  loss). Fixed with tests (§9).

---

## 2. Tooling added (all generic, all tested)

| Tool | Purpose | Tests |
|---|---|---|
| `scripts/analyze_semaphore_timeline.py` | Full lifecycle of any semaphore from a log (create/signal/wait/wake/timeout, streaks, waiter/signaler RIPs, thread mapping). `--handle/--name/--caller`, `--json`. | 18 |
| `scripts/analyze_run_report.py` | Per-title compatibility triage report: presents, missing symbols, unresolved NIDs, failing HLE codes, exception fingerprints, stall candidate, first-causal-blocker. `--json`. | 9 |
| `scripts/analyze_waitfor_graph.py` | Offline wait-for graph + deadlock-cycle / cross-wait detection from a log (the offline form of the live sync snapshot in §12). | 7 |

Run them:

```bash
python3 scripts/analyze_semaphore_timeline.py <log> --handle 0x86
python3 scripts/analyze_run_report.py <log>
python3 scripts/analyze_waitfor_graph.py <log>
python3 -m unittest scripts.test_analyze_semaphore_timeline scripts.test_analyze_run_report scripts.test_analyze_waitfor_graph
```

---

## 3. 0x86 lifecycle, producer & waiter identity

From `analyze_semaphore_timeline.py --handle 0x86`:

- **Created** as `Baselib_SystemSemaphore attr=0x1 init=0 max=2147483647`.
- **Waiter (consumer):** `ret=0x800D18129`, host-blocked (`guest=0`), `need=1`,
  `timeout=1000` µs. Repeatedly `wait-host-block` → `TIMED_OUT` → retry.
- **Signaller (producer):** `ret=0x800D17D53`, always from the
  `Loading.PreloadManager` thread (guest pointer differs per run by ASLR but the
  thread name is invariant).
- The producer's own function pairs **signal 0x86 (`0x800D17D53`)** with **wait
  0x84 (`0x800D17CFB`)** — the two RIPs are 0x58 apart, i.e. a
  "hand off work on 0x86, then wait for the acknowledge on 0x84" handshake.
  `0x84` is only ever signaled from `ret=0x800D1379F` (the consumer side).

This is a **ping-pong**: producer `signal 0x86 → wait 0x84`; consumer
`wait 0x86 → … → signal 0x84`.

---

## 4. GOOD vs BAD comparison (from the tools)

| Run (config) | 0x86 signal | 0x86 wake | 0x86 timeout | longest streak | >1 s parked GC deliveries | outcome |
|---|---|---|---|---|---|---|
| `run_fix.log` (pre-V2, ff66b25+28cab08) — **GOOD** | 1 | 1 | 15 | 12 | 33 (all short) | reached gameplay; log **cut short** at 14 416 lines |
| `run_stage12_17687b7_1` (V2-on) | 6 | 6 | 41 | 28 | 526 | wedge |
| `run_stage12_17687b7_2` (V2-on) | 7 | 7 | 49 | 36 | 532 | wedge |
| `run_stage12_17687b7_3` (V2-on) | 8 | 8 | 38 | 28 | 600 | wedge |
| `run_stage12_17687b7_guardoff_1` (V2-on, guard off) | 6 | 6 | 46 | — | 412 | wedge |
| `run_stage12_default_3` (V2-off default) | **0** | **0** | **10 820** | huge | **0** | never handshakes |

Key reads:

1. **`signal == wake` in every V2-on run.** No dropped signal ⇒ the primitive is
   sound and there is no lost wakeup on 0x86. (Independently reconfirmed by the
   primitive audit, §8.)
2. **The producer signals *more* in BAD than in GOOD** (6–8 vs 1). This kills the
   old "producer starvation" framing for the V2-on shape: the producer runs and
   signals repeatedly, then the *mutual* handshake wedges.
3. **Guard-off changes nothing** (6 signals, 412 GC pauses) ⇒ the import-loop
   guard is **not** the cause of the 0x86 stall (it is a separate defect, §9).
4. **The GOOD log is not proof of indefinite health** — it ends ~1 500 lines
   after 0x86 is created (killed after gameplay was visually confirmed), with no
   timeout flood. What distinguishes BAD is the **sustained timeout flood + the
   multi-second GC pauses**, neither of which GOOD exhibits.
5. **V2-off default is a *different* failure**: 0x86 is **never signaled once**,
   the waiter floods 10 820 timeouts, and there are **zero** parked GC-suspend
   deliveries — the guest never even reaches the first handshake / never enters
   stop-the-world. Do not merge this with the V2-on wedge.

> Config attribution (V2-on/off) follows the stage-12 capture recipe and file
> naming; the env flags themselves are **not** recorded in the logs. See §11.

---

## 5. Earliest causal divergence (V2-on shape)

Traced with the timeline + wait-for tools on `run_stage12_17687b7_1`:

- Last successful 0x86 signal: **line 21909**. Last successful 0x84 signal (the
  acknowledge): **line 21945**. After 21945, **0x84 is never signaled again.**
- `Loading.PreloadManager` keeps running for ~20 000 more lines (it services
  other handshakes — 0x18, 0x4F, 0x22 — and handles many 0x1E GC-suspends), then
  at **line 42214** it does `wait-block 0x84 timeout=infinite` (`ret=0x800D17CFB`)
  and **blocks there permanently** (its remaining events are only the internal
  Suspend/Resume signals emitted *by* an in-flight 0x1E delivery over that park).
- So the **first** thread to get permanently stuck is the **0x86 consumer**
  (right after 21945, waiting for a 0x86 signal that never comes); the producer
  only *joins* the deadlock at 42214 when it finally needs the 0x84 acknowledge.
- At 42214 a 0x1E GC-suspend is delivered while PreloadManager is `mode=parked`
  on the 0x84 wait, taking **6 285 ms** — i.e. the whole IL2CPP world stayed
  stopped for 6.285 s because a mutator could not make progress.

**Verdict:** this is a genuine *mutual* wait (higher-level Unity job/scene
deadlock), not a dropped signal. `0x84` is genuinely never re-posted; the 0x86
consumer uses a finite timeout so it structurally cannot be a lost-wake victim.

---

## 6. Lost-wake vs producer-starvation verdict

| Hypothesis | Verdict | Evidence |
|---|---|---|
| Broken semaphore primitive | **DISPROVEN** | `signal==wake` every run; primitive audit §8; timeout provably does not consume count (regression test). |
| Lost wakeup on 0x86 | **DISPROVEN** | finite 1000 µs waiter re-checks every ~1 ms; would recover instantly if a signal ever landed. |
| Lost wakeup on 0x84 (across GC suspend) | **DISPROVEN** | 0x84 is never signaled after line 21945 at all — nothing to lose; GC-suspend race window is closed in code (§7). |
| AGC `suspendPoint` starves the producer | **DISPROVEN** | `suspendPoint` is a no-op export; log line is guest-emitted (§7). |
| Import-loop guard kills the producer | **DISPROVEN** | guard-off run stalls identically. |
| Higher-level producer/consumer mutual wedge | **PROVEN (structure)** / **LIKELY (exact trigger)** | §5; nondeterministic onset (6/7/8 rounds). |
| V2-off "never handshakes" is the *same* bug | **DISPROVEN** | different shape (signal=0, no GC pauses) — a distinct, earlier blocker. |

---

## 7. GC-suspend / suspendPoint / host-park audit (read-only)

- **`sce::Agc::suspendPoint` is a no-op** (`AgcExports.cs`, `SuspendPoint` sets
  RAX=0 and returns). The `TRC R4089 / Forcing call` string is the *guest's*
  libSceAgc satisfying a Sony graphics TRC; it exists in-repo only as a
  log-parser fixture. **It is unrelated to CPU thread suspension.**
- The 0x1E GC-suspend / host-park / resume machinery (from `28cab08`) is sound
  for the pre-V2 default path. The critical lost-wake window (a signal landing
  while a parked waiter is temporarily marked `Running` for suspend delivery) is
  explicitly closed by `RestoreInterruptedGuestThread` re-running
  `BlockWaiter.TryWake()` (`DirectExecutionBackend.cs:5170-5198`). Verdicts:
  producer-skips-signal, stale-context, skipped-work, two-primitive-ordering-hole,
  lost-wakeup — all **DISPROVEN** for the pre-V2 path.
- **One residual asymmetry (POSSIBLE, benign):** the host-park delivery path
  lacks the follow-up drain the cooperative path has (`:5257-5277`); a *second*
  0x1E raised against a thread whose first 0x1E delivery is still in flight is
  deferred (drained at the next safe-point / park-wake), not lost. IL2CPP
  stop-the-world does one raise per thread per cycle, so real-world risk is low.
  Tracked, not fixed.
- **`elapsed_ms` semantics:** it measures the time spent *running the guest's
  0x1E handler* (which nested-waits on `ResumeSemaphore`). The 6 s values are
  therefore guest stop-the-world durations — a downstream symptom of the wedge,
  not an emulator delivery cost.

---

## 8. Semaphore primitive audit (read-only)

`KernelSemaphoreCompatExports.cs`: create/wait/signal/poll/cancel/delete.

- Count transitions on signal/wait, `maxCount` overflow (→ `EINVAL`), timeout
  conversion (µs → ticks, 1000 µs handled, no off-by-one), signal-before-wait
  persistence, host-block vs cooperative paths sharing one gated count: all
  **CORRECT**.
- **Timeout does not consume the count** — the self-healing property that makes
  the 0x86 consumer's 1 ms retry benign. Now pinned by
  `KernelSemaphoreSemanticsTests.TimedOutWaitDoesNotPoisonSemaphore`.
- Real but **out-of-scope** gaps (not the 0x86 bug), logged for the backlog:
  - **FIFO/`attr` ignored**: `attr=0x1` (TH_FIFO) is validated but not stored;
    multi-waiter wake order is dictionary order. Single-waiter (Baselib) is
    unaffected.
  - **`CancelSema`** wakes waiters but they return OK instead of `ECANCELED`.
  - **`DeleteSema`** does not tear down blocked (esp. infinite) waiters.

New regression coverage added: `KernelSemaphoreSemanticsTests` (11 tests).

---

## 9. Import-loop guard: defect + fix (landed)

`ShouldForceGuestExitOnImportLoop` fired on **(repeating pattern + 5 s
wall-clock) alone**, with no notion of whole-VM progress, and its recorded
import stream **excludes guest-worker dispatches**. A legitimate hot
`scePthreadYield` spin on one thread — while other threads do real work — was
therefore indistinguishable from a livelock and got force-unwound around import
#25 M. The specific termination reason was then destroyed by the nested-scope
`LastError` save/restore and the ThreadStatic `ActiveForcedGuestExit` flag being
set on a worker thread, degrading to `"unknown backend error"`.

**Fix (this stage):**
- **Fix A — global progress gate.** Extracted a pure, unit-tested
  `DirectExecutionBackend.ShouldFireImportLoopGuard(...)` that vetoes and
  restarts the suspicion window when *other* threads dispatched imports during
  it (per-thread counter + existing global `_importDispatchCount`). No per-title
  or per-NID exception.
- **Fix B — reason preservation.** The specific reason is stored in a dedicated
  instance field that survives the unwind and is consulted at the top level even
  when the ThreadStatic flag landed on a worker thread.
- **Tests:** `ImportLoopGuardTests` (5). The progress case would have fired under
  the old progress-blind logic — the fix is captured.

This is independent of the 0x86 stall (guard-off runs still wedge).

---

## 10. AGC shader diagnostics (landed)

`sceAgcCreateShader` returned `MEMORY_FAULT` from short-circuit relocation chains
without naming the failing field, and nothing was logged under
`SHARPEMU_LOG_AGC_SHADER`. Relocation failures now log the exact field
(`cx@0x18`, `sh@0x20`, `userdata@0x08`, … `userdata_sub@0x00..`), the stage
(read/write), and the field/delta/target addresses; each MEMORY_FAULT return
emits a phase line. Gated; success/failure semantics unchanged. Header-validation
tests added (`AgcCreateShaderTests`, 3). This prepares the next rare
`sceAgcCreateShader` NULL-descriptor AV reproduction (the *separate* AV family)
without needing a live session — the trace will now name the offending field.

---

## 11. HEAD vs 17687b7 change matrix (read-only)

`17687b7..HEAD` = 34 commits; only 7 touch production `src/`, and every
execution-routing change (`045212e` ordinary-pthread→worker, `1896df6` rented
0x1E, `b908e41`/`b260bdf` module-init) is gated behind
`SHARPEMU_NATIVE_GUEST_V2=1` (default **off**). The only non-gated default-path
delta is `7fb127e` (grow-on-demand worker pool with a run-slot blocking
semaphore), which serves only `tbb_thead` — not Cocoon's inline Baselib path.
`e8cfe79`/`3bd5dcf` are inert diagnostics. **For the default configuration the
0x86 producer/consumer path is effectively unchanged from `17687b7` to HEAD**;
the observable difference between GOOD `run_fix` and the stalling captures is
dominated by the **env flags used**, not a default-path code delta in this range.

---

## 12. Live sync-stall snapshot — design (not yet implemented)

The offline `analyze_waitfor_graph.py` validates the data model. The live
equivalent (Task 6/7) should be:

- **Gating:** `SHARPEMU_DIAG_SYNC_SNAPSHOT=1` (one-shot on demand) and
  `SHARPEMU_DIAG_STALL_SNAPSHOT_MS=<n>` (auto after `n` ms of no *frame/present*
  progress). **Disabled by default; no behavioral intervention; no forced
  unwind; rate-limited to one snapshot.**
- **Progress signal:** must track real present/frame activity, *not* import
  repetition (the import counter keeps climbing during the timeout flood — see
  §4 — so it is a false liveness signal). Reuse `MarkExecutionProgress`
  plumbing (`DirectExecutionBackend.cs:7026`) but keyed on present, not dispatch.
- **Snapshot content per logical guest thread:** current/last guest RIP, last
  import NID, wait object (handle + wake key), native-worker vs inline, host-park
  state, pending 0x1E/suspend flags, and the semaphore wait-for edges (which
  thread waits on which handle, last signaler of each) — exactly the
  `analyze_waitfor_graph.py` data model.
- **Emit once** as `[LOADER][DIAG] sync_snapshot ...` lines that the offline tool
  can already parse.

Design note: because host-blocked waiters carry `guest=0`, the live snapshot
must read the *actual* parked-thread registry (`_hostParkRegistry`) to attribute
anonymous waiters — the piece the offline tool cannot recover. That is the main
value the live version adds over the offline reconstruction.

---

## 13. Morning Windows verification

The default runtime path is Windows-native. To confirm the landed fixes and
gather the next 0x86 evidence, from a **Windows** checkout of `gpt-dlsym`:

```powershell
# 1. Build
dotnet build .\SharpEmu.slnx -c Debug

# 2. Unit tests landed this stage (should all pass)
dotnet test .\tests\SharpEmu.Libs.Tests\SharpEmu.Libs.Tests.csproj `
  --filter "FullyQualifiedName~ImportLoopGuard|FullyQualifiedName~KernelSemaphoreSemantics|FullyQualifiedName~AgcCreateShader"

# 3. Cocoon capture with the sema + exception + guard-off + AGC-shader trace flags
$env:SHARPEMU_LOG_SEMA=1
$env:SHARPEMU_LOG_GUEST_EXCEPTIONS=1
$env:SHARPEMU_DISABLE_IMPORT_LOOP_GUARD=1
$env:SHARPEMU_LOG_AGC_SHADER=1
dotnet run --project .\src\SharpEmu.CLI -c Debug -- `
  .\real-tests\Cocoon\PPSA08766-app0\eboot.bin *> run_stage13.log
```

Then, back on any machine:

```bash
python3 scripts/analyze_run_report.py run_stage13.log
python3 scripts/analyze_semaphore_timeline.py run_stage13.log --handle 0x86
python3 scripts/analyze_waitfor_graph.py run_stage13.log
```

**Expected fingerprints:**
- If the V2-on wedge recurs: `0x86 signal==wake` (6–8), then a terminal timeout
  flood; `analyze_waitfor_graph` shows the render thread blocked and a
  cross-wait on `Loading.PreloadManager` (blocked on `0x84`, producer of `0x86`).
- If a rare `sceAgcCreateShader` MEMORY_FAULT occurs, the log now contains
  `agc.create_shader.reloc_fail field=<label>` naming the exact failing field.
- The import-loop guard should no longer force-unwind while other threads
  progress; if it ever does fire, the error now names the real
  `import#…(nid)…` cause instead of "unknown backend error".

**The decisive open experiment** (still requires Windows) is a current-HEAD
**pure default (V2 OFF, no env flags)** Cocoon run ×5 to settle
regression-vs-nondeterminism for the V2-off "never handshakes" shape — see §4/§11.

---

## 14. Confidence ledger

| Claim | Level |
|---|---|
| 0x86 primitive is sound; no lost wake on 0x86 | **PROVEN** |
| Producer = Loading.PreloadManager `@0x800D17D53`; waiter `@0x800D18129` | **PROVEN** |
| V2-on terminal state is a higher-level mutual wedge (0x86/0x84) | **PROVEN (structure)** |
| Exact non-deterministic trigger of the wedge | **LIKELY** |
| 6 s GC pauses are a symptom, not a primitive bug | **PROVEN** |
| `suspendPoint`/R4089 is a red herring | **PROVEN** |
| Import-loop guard progress-blindness + reason loss | **PROVEN** (fixed) |
| Host-park missing follow-up drain (nested double-suspend) | **POSSIBLE** (benign) |
| V2-off "never handshakes" is a distinct, earlier blocker | **PROVEN (distinct)**; root **cause open** |
