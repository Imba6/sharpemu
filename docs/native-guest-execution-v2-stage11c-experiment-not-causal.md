# Stage 11C — the rented-worker experiment is NOT the Cocoon blocker; Stage-11B producer-starvation is DISPROVEN

Status: **analysis + analyzer/parser improvement only, no production runtime change** (branch
`gpt-dlsym`, not pushed by this session). Two new Windows-native captures (rented-worker + sema trace;
and no-experiment) overturn the Stage-11B hypothesis. **STOP conditions are met — no execution-routing
change is warranted.**

## The three captures compared (all reached first frame; all have `SHARPEMU_LOG_SEMA=1`)

| | GOOD `run_fix.log` (inline, no exp) | A `run_stage11b_sema.log` (rented-worker) | B `run_stage11b_noexp.log` (no experiment) |
|---|---|---|---|
| sema 0x86 (`Baselib_SystemSemaphore`) **signals** | **1** | **7** | **13** |
| 0x86 host-blocks / host-wakes | 16 / 1 | 59 / 7 | 2501 / 13 |
| suspendPoints | **33** | 13 | 13 |
| pipeline-cache saves | **3** | 1 | 1 |
| ended by user `videoout-window-closed` | no | **yes** | **yes** |
| outcome | **sustained gameplay** | **stalled** | **stalled** |

## 1–3. Rented-worker producer fate (Log A)

**The producer runs.** `sema.signal handle=0x86 name='Baselib_SystemSemaphore'` fires **7×** from a real
guest thread `guest=0x1EA499CF2E0 ret=0x800D17D53`; the host-parked waiter is woken 7× (`wait-host-wake`).
0x86 activity ends at L12904 of 36099 — **the run gets past the 0x86 handshake** and then stalls
elsewhere (the analyzer now flags a later stuck fence on handle `0x18` caller `0x805CD8F47`). Final
state: user closed the window.

## 4–7. No-experiment A/B (Log B)

**No-experiment does NOT reach gameplay** — it stalls the same way (and worse): 13 suspendPoints, 1
cache save, ended by user window-close. sema 0x86 **is** signaled **13×** (`guest=0x26022945530
ret=0x800D17D53`), yet the host-parked waiter (`guest=0x0`, `ret=0x800D18129`, 1000µs poll)
re-blocks 2501× and the game never sustains gameplay. Good-run signal thread/RIP:
`guest=0x2C78913D0D0 ret=0x800D17D53` (same call site in all runs).

## 8–10. First divergence, why the producer "fails", pool

- **The producer does NOT fail** — it signals 0x86 in every config (1/7/13×). The Stage-11B claim
  ("`SignalSema(0x86)` essentially never runs → producer starvation under the experiment") was an
  artifact of the earlier `run_stage11_guardoff.log` having `SHARPEMU_LOG_SEMA` **off**: only the
  timed-out WARN was visible, so the (present) signals were invisible and wrongly inferred to be zero.
  **Corrected: the semaphore is signaled and the waiter is woken in all runs.**
- **First divergence GOOD vs stalled:** the good run needs the 0x86 handshake only ~16× and proceeds
  to sustained gameplay (33 suspends, 3 cache saves); A and B re-cycle Baselib synchronization
  (0x86 host-park poll and an infinite cooperative wait on a second Baselib sema **0x84**,
  `ret=0x800D17CFB`) far more (59 / 2501 blocks) and never sustain gameplay. The divergence is a
  higher-level Unity job/scene-progress stall, **not** a lost signal and **not** the semaphore layer.
- **Pool exhaustion remains disproven** (26/128 peak, 0 rent timeouts) — and is now moot, since the
  producer runs regardless.

## 11. Is the rented-worker experiment still necessary / is it the cause?

**It is NOT the cause of this stall.** No-experiment (B) stalls at least as badly as rented-worker (A)
— B has ~5× more Baselib host-blocks. So `SHARPEMU_EXPERIMENT_0X1E_RENTED_WORKER` is **orthogonal** to
this blocker. These logs therefore neither prove the experiment obsolete nor prove it necessary for
gameplay (both configs stall); the architectural "is the experiment still needed for 0x1E safety"
question is **separate and not answerable from this data** (both runs show 0x1E `guest_exception`
deliveries). No conclusion that would justify changing execution routing follows from these captures.

## 12. Recommended architectural action

**None to execution routing.** Per the milestone STOP conditions — *no-experiment does NOT reach
gameplay* and *both configurations behave equivalently* — do not patch the semaphore, the scheduler, or
the rented-worker routing on this evidence. The real, still-open question is a **regression-vs-
non-determinism** one:

- `run_fix.log` (captured 2026-08-20) reached sustained gameplay on the inline path with the **same**
  Baselib 0x86 handshake, needing it only ~16×.
- Current-HEAD runs (both configs) get stuck re-cycling Baselib synchronization and never sustain
  gameplay.

So either (a) a change between the `run_fix` era and current HEAD regressed higher-level progress, or
(b) the Baselib job/scene handshake is a race that `run_fix` won and these runs lost. Both are
plausible; the evidence does not single one out ⟹ **STOP**.

## 13–14. Regression / production change

**None.** No unique emulator defect is proven (the semaphore primitive is correct — signals flow,
wakes fire; the producer runs). A fix now would be forcing higher-level progress, which is disallowed.

## 15–16. Cocoon / cross-title

Cocoon: three configs characterized above; only the older `run_fix` reached gameplay. Cross-title: not
run (no proven fix to validate).

## 17. Analyzer / tests (this pass)

- `scripts/analyze_stage10b_capture.py`: (a) `sema.wait-host-block` floods on one `(handle, caller)`
  now count as a stuck fence (SHARPEMU_LOG_SEMA runs log host-blocks instead of timed-out WARNs, which
  previously hid the stall); (b) parse and report `videoout-window-closed` (user close) and
  pipeline-cache-save count; (c) **strengthened the gameplay verdict** — `reached sustained gameplay`
  now requires suspendPoint ≥ 25 **and** ≥ 2 cache saves **and** no stuck fence (suspendPoint alone
  mislabeled the 13-suspend stalls as gameplay). All four Cocoon logs now classify correctly (only
  `run_fix` = gameplay).
- `scripts/test_analyze_stage10b_capture.py`: 8 tests, all pass (added host-block-flood + window-close
  regression; updated good-run fixtures to the stronger signal).

## 18. Commits (branch `gpt-dlsym`, not pushed)

1. analyzer host-block/window-close/gameplay-threshold improvement + tests.
2. this document.

## 19. Next blocker / next smallest milestone

The real open blocker is **Cocoon higher-level progress stalls in Unity Baselib job/scene
synchronization after the render loop starts**, independent of the rented-worker experiment. Next:
1. **Regression-vs-nondeterminism:** re-run current HEAD inline (no experiment, no verbose) **several
   times** — if it sometimes reaches gameplay, it is a race; if never, bisect between the `run_fix`
   (2026-08-20) commit and HEAD for a progress regression.
2. If reproducible, characterize what the game waits on after first frame with a **narrow** trace of
   the Baselib job fences (semaphores 0x84/0x86 and the job/event-flag system) plus GC/scene markers —
   without the heavy global guest-thread log — to find the higher-level operation that never
   completes. This is a new milestone (Unity job/scene progress), not semaphore/routing.

## git status

Branch `gpt-dlsym`. Modified: `scripts/analyze_stage10b_capture.py`,
`scripts/test_analyze_stage10b_capture.py`. New: this doc. The captures live under
`real-tests/Cocoon/PPSA08766-app0/` (git-ignored, not committed).

## NOTHING WAS PUSHED BY THIS SESSION.

## Previous `origin/gpt-dlsym` advances were the user's manual checkpoint pushes.
