# Stage 12 — regression vs nondeterminism: the Stage-11 A/B was confounded; the untested variable is V2 itself

Status: **read-only analysis + analyzer fix only, no production runtime change** (branch `gpt-dlsym`,
not pushed by this session). The regression-vs-nondeterminism question **cannot be answered from
here** — it requires Windows-native repeat runs (Phases 2–4). But Phase 1 (historical revision), Phase
5 (commit-range audit), and Phases 8–10 (Baselib handle identity) reframe the question decisively.

## The key reframe

`run_fix.log` (the only gameplay-reaching capture) was taken **2026-08-20 17:38 — before the entire
V2 native-worker line landed** (V2 stage 2 `7fb127e` is 2026-08-21). Every stalling capture (Stage
10B/11/11B/11C) set **`SHARPEMU_NATIVE_GUEST_V2=1`**. Crucially, the Stage-11C "no-experiment" A/B
only removed `SHARPEMU_EXPERIMENT_0X1E_RENTED_WORKER` — **it left V2 on**. So it was **not** equivalent
to `run_fix`; it still routed ordinary guest pthreads onto native workers (V2 stage 3 `045212e`).

⟹ The A/B that actually isolates the regression has **never been run**: **current HEAD with V2 OFF
(pure default — no `SHARPEMU_NATIVE_GUEST_V2`, no experiment, no guard flags)**. `NativeGuestV2Enabled`
is **off by default** (`DirectExecutionBackend.NativeWorker.cs:41`), so the default path is the same
execution model `run_fix` used, plus later fixes.

## 1–2. Historical GOOD revision (Phase 1)

- **`GOOD_BASE_COMMIT` = narrow range `ff66b25` (2026-08-20 15:23) .. `28cab08` (2026-08-20 17:51)**,
  bracketing `run_fix.log`'s 17:38 mtime. (User builds are local/uncommitted, so an exact SHA is not
  provable; this is the tightest justified range. `28cab08` "deliver kernel exceptions to host-parked
  guest threads" landed 13 min after the capture, so `run_fix` is most likely at `ff66b25` ± a dirty
  tree.)
- **Config:** pre-V2 inline. No `SHARPEMU_NATIVE_GUEST_V2`, no `SHARPEMU_EXPERIMENT_0X1E_RENTED_WORKER`,
  no dedicated-primary patch, import-loop guard at its default (these gates did not exist yet).
  `SHARPEMU_LOG_SEMA=1` was on (the log has `sema.*` traces). Title Cocoon PPSA08766 v01.004.000,
  firmware 0x07000038.

## 3–4. Repeated-run results

**Not available** — I cannot run Windows-native Cocoon from this WSL/Linux session (GPU-interactive,
non-deterministic, and a Windows build here would corrupt the shared `artifacts/obj`). Phases 2–4 are
handed to the user with the recipes below. **Neither regression nor nondeterminism is yet proven.**

## 5. Regression / 6. Nondeterminism

Both **UNPROVEN**. The evidence to date only shows V2-ON configs stalling and one pre-V2 capture
reaching gameplay — which is consistent with *either* a V2 regression *or* the default path also
having regressed *or* nondeterminism. The decisive discriminator is the V2-OFF run (§Recipe A).

## 7. Commit-range audit (Phase 5) — runtime-capable commits `28cab08..HEAD`

Docs/tests/analyzer commits excluded (no runtime effect). Split by whether they affect the **default
(V2-off) path** or only the **gated V2 path**:

**Gated — only active with `SHARPEMU_NATIVE_GUEST_V2=1` (so they explain V2-on stalls, NOT a default
regression):**
- `7fb127e` V2 stage 2 — native-worker pool
- `045212e` V2 stage 3 — **route ordinary guest pthreads onto native workers** (the core execution-model change)
- `1896df6` V2 stage 7 — rented-worker 0x1E experiment (needs the extra flag too)
- `b908e41` V2 stage 8 — route boot module initializers onto workers

**Non-gated — affect the DEFAULT inline path, landed AFTER `run_fix` (17:38); regression candidates if
V2-off also stalls:**
- `291de64` (2026-08-20 23:29) **VEH cooperative-mode fault pre-filter + continuation-stability** — changes default-path fault handling.
- `2ff1d2a` **pthread: abandon mutexes in the SyncRoot domain and hand off to waiters** — touches the exact mutex hand-off Unity Baselib relies on; **strongest default-path candidate.**
- `17687b7` fix(loader): boot-init deferred FMOD plugins (DT_INIT).
- `989acd4` + `0cd2fb2` memory zero-on-reuse **and its revert** — net zero, ignore.
- `4a2f3ab` CLI mitigation relaunch (launch path only), `5f69b92` aerolib build (build only), `069a8e8` diag — not execution semantics.

## 8–9. Baselib handle identity (0x84 / 0x86 / 0x18)

**All three are `Baselib_SystemSemaphore`** (`attr=0x1 init=0 max=2147483647`) — Unity recycles a small
pool of system semaphores for many job/thread handshakes, so a handle number is **not** a distinct
logical fence. Evidence from `run_stage11b_sema.log`:

- **0x86**: the render/scene consumer (`ret=0x800D18129`) host-park polls it; producer releases at
  `ret=0x800D17D53`. Signaled 7× (rented-worker) / 13× (no-exp) / 1× (good). Got **past** it in LOG A
  (host-wakes stop at L12904).
- **0x84**: the 0x86 producer thread (`guest=0x1EA499CF2E0`) itself waits on 0x84 **cooperatively,
  infinite** (`ret=0x800D17CFB`), 6 blocks / 6 wakes / 6 signals — a serviced producer/consumer cycle.
- **0x18**: a **heavily-used, fully-serviced** job semaphore — 853 signals, 296 wait-wakes + 310
  host-wakes vs 310 host-blocks (`signal ret=0x805D55015`, many waiter call sites). **Not stuck** — my
  first host-block detector wrongly flagged it; fixed this pass by netting host-blocks against wakes.

So the handles are Baselib's generic pool; the "stuck" one in a run is whichever recycled instance the
render/scene consumer is parked on when higher-level progress halts. This is a **higher-level Unity
job/scene progress** phenomenon, not a per-handle semaphore defect.

## 10. Higher-level timeline (GOOD vs stalled)

`BOOT → 9 module inits → first frame → asset preload completes (87 levels + StreamingAssets) → render
loop starts (suspendPoints, pipeline cache) → Baselib job/scene handshakes`. The **first divergence**
is here: GOOD clears the handshakes quickly (16 Baselib 0x86 blocks → 33 suspendPoints, 3 cache saves,
sustained gameplay); the V2 runs re-cycle them (59–6576 blocks → 13 suspendPoints, 1 cache save, then
the user closes the stalled window). No earlier milestone differs.

## 11–16. Offending commit / root cause / fix

**Not yet determinable — STOP.** Per the milestone STOP conditions (regression unproven; more than one
plausible cause; can't run the discriminating config here), **no production change**. The candidate
set is narrowed to: *(a)* V2 native-worker routing (`045212e` et al.) if V2-off reaches gameplay, or
*(b)* a default-path commit (`2ff1d2a` pthread-mutex, `291de64` VEH, `17687b7` FMOD) if V2-off also
stalls. No synthetic regression is written until the discriminator picks a branch.

## 17–18. Cocoon / cross-title

Cocoon: characterized; only pre-V2 `run_fix` reached gameplay. Cross-title: N/A (no fix).

## 19–20. Analyzer / tests / commits (this pass)

- `scripts/analyze_stage10b_capture.py`: host-block-flood stuck detection now **nets host-blocks
  against host-wakes per handle** (a busy-but-serviced Baselib fence like 0x18 is no longer a false
  positive); genuine timed-out WARNs still count directly.
- `scripts/test_analyze_stage10b_capture.py`: 8 tests, all pass (host-block-flood test now models the
  no-wake case).
- Commits (branch `gpt-dlsym`, not pushed): (1) analyzer net-of-wakes fix; (2) this document.

## 21. Next blocker / decisive next steps (Windows-native; user)

**Recipe A — the missing discriminator (do this FIRST; cheap, no worktree):** current HEAD, **pure
default config — NO env flags at all** (V2 off, no experiment, no guard override, no verbose except
optionally `SHARPEMU_LOG_SEMA=1`). Run **≥5 times**. Log → `run_stage12_default_N.log`.
- If it **reaches sustained gameplay** (≥25 suspendPoints, ≥2 cache saves) in any run → the default
  path is healthy; **V2 is the regressor** (or unnecessary for Cocoon). Next: investigate V2 stage-3
  native-worker routing (`045212e`) interaction with Baselib job progress, or simply stop enabling V2
  for this title.
- If it **also stalls** in all runs → a **default-path regression** since `run_fix`. Bisect the three
  non-gated candidates in a **separate worktree** (never touch main `gpt-dlsym`):
  `git worktree add ../vps5-good <rev>`; test `2ff1d2a^` vs `2ff1d2a`, then `291de64`, then `17687b7`;
  ≥3 runs each; clean Windows build per rev (do not reuse `artifacts/obj`).

**Recipe B — historical confirmation (optional):** worktree at `ff66b25`, clean Windows build, run ≥3×
with the pre-V2 config. If it no longer reaches gameplay, `run_fix` was nondeterministic/environment —
STOP the regression hypothesis and characterize the Baselib race instead.

The analyzer already classifies all these outcomes (gameplay vs stuck-fence vs did-not-reach).

## 22. git status

Branch `gpt-dlsym`. Modified: `scripts/analyze_stage10b_capture.py`,
`scripts/test_analyze_stage10b_capture.py`. New: this doc. Captures git-ignored (not committed).

## NOTHING WAS PUSHED BY THIS SESSION.

## Previous `origin/gpt-dlsym` advances were the user's manual checkpoint pushes.
